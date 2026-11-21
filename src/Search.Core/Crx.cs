using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Search.Core;

/// Chrome extensions from the Chrome Web Store: the store's package is a zip
/// with a signed header in front (CRX3). The signature is checked against the
/// archive and the key's id before anything is unpacked, exactly as Chrome and
/// the Mac app do; a package that fails is never installed.
public static partial class Crx
{
    [GeneratedRegex("(?:^|[/=])([a-p]{32})(?:$|[/?#&])")]
    private static partial Regex IdPattern();

    /// The bytes CRX3 signs before the header and archive.
    static readonly byte[] Context = Encoding.ASCII.GetBytes("CRX3 SignedData\0");

    /// The extension's id from a store link, or the bare id itself.
    public static string? IdFrom(string text)
    {
        text = (text ?? "").Trim();
        // Chrome ids are lower-case a-p; accept an upper-case paste too.
        text = text.ToLowerInvariant();
        if (text.Length == 32 && text.All(c => c >= 'a' && c <= 'p')) return text;
        if (!text.Contains("chromewebstore.google.com") && !text.Contains("chrome.google.com/webstore")) return null;
        var m = IdPattern().Match(text);
        return m.Success ? m.Groups[1].Value : null;
    }

    /// The store's CRX3 endpoint. Modern Chrome versions matter to some
    /// extensions; the port asks for CRX3 only.
    public static Uri DownloadUrl(string id, string chromeVersion = "156.0.0.0") =>
        new($"https://clients2.google.com/service/update2/crx?response=redirect&prodversion={chromeVersion}" +
            $"&acceptformat=crx3&x=id%3D{id}%26uc");

    /// Where the zip starts inside a CRX, or 0 when it is already a zip.
    /// Parsing only: Install never trusts this on its own.
    public static int ZipOffset(ReadOnlySpan<byte> data)
    {
        if (data.Length >= 4 && data[0] == 'P' && data[1] == 'K') return 0;
        if (data.Length < 16 || data[0] != 'C' || data[1] != 'r' || data[2] != '2' || data[3] != '4')
            throw new InvalidDataException("Not a Chrome extension package.");
        uint version = BitConverter.ToUInt32(data[4..8]);
        if (version == 3) return checked(12 + (int)BitConverter.ToUInt32(data[8..12]));
        if (version == 2) return checked(16 + (int)BitConverter.ToUInt32(data[8..12]) + (int)BitConverter.ToUInt32(data[12..16]));
        throw new InvalidDataException($"Unknown package version {version}.");
    }

    /// Verifies a CRX3 package and, only then, unpacks it into `folder`,
    /// replacing what was there. Throws InvalidDataException on anything
    /// unsigned, tampered with, or signed for another id.
    public static void Install(byte[] crx, string folder, string expectedId)
    {
        if (crx.Length < 16 || crx[0] != 'C' || crx[1] != 'r' || crx[2] != '2' || crx[3] != '4')
            throw new InvalidDataException("Not a Chrome extension package.");
        uint version = BitConverter.ToUInt32(crx, 4);
        if (version != 3) throw new InvalidDataException($"CRX version {version} is not accepted; only CRX3 is signed.");
        uint headerSize = BitConverter.ToUInt32(crx, 8);
        if (headerSize > int.MaxValue || 12L + headerSize > crx.Length)
            throw new InvalidDataException("Truncated CRX header.");
        var header = crx.AsSpan(12, (int)headerSize);
        var zip = crx.AsSpan(12 + (int)headerSize);
        if (zip.Length < 4) throw new InvalidDataException("The package has no archive.");

        var (signedHeader, proofs) = ParseHeader(header);
        var declared = CrxId(signedHeader);
        Verify(zip, signedHeader, proofs, declared);
        if (!string.Equals(declared, expectedId, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"The package is signed for {declared}, not {expectedId}.");

        UnpackAtomic(zip, folder);
    }

    /// The popup page a toolbar button would open, from manifest.json.
    public static string? Popup(string folder)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "manifest.json")));
            foreach (var key in new[] { "action", "browser_action", "page_action" })
                if (doc.RootElement.TryGetProperty(key, out var a) && a.TryGetProperty("default_popup", out var p) && p.GetString() is { Length: > 0 } s)
                    return s.TrimStart('/');
        }
        catch { }
        return null;
    }

    public static string? OptionsPage(string folder)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "manifest.json")));
            if (doc.RootElement.TryGetProperty("options_page", out var o) && o.GetString() is { Length: > 0 } s) return s.TrimStart('/');
            if (doc.RootElement.TryGetProperty("options_ui", out var ui) && ui.TryGetProperty("page", out var p) && p.GetString() is { Length: > 0 } t) return t.TrimStart('/');
        }
        catch { }
        return null;
    }

    // ---------------------------------------------------------------- header

    static (byte[] Signed, List<(byte[] Key, byte[] Sig)> Proofs) ParseHeader(ReadOnlySpan<byte> header)
    {
        byte[]? signed = null;
        var proofs = new List<(byte[] Key, byte[] Sig)>();
        int i = 0;
        while (i < header.Length)
        {
            ulong tag = ReadVarint(header, ref i);
            int field = (int)(tag >> 3);
            int wire = (int)(tag & 7);
            if (wire != 2) { Skip(header, ref i, wire); continue; }
            var payload = Field(header, ref i);
            if (field == 10000) signed = payload.ToArray();
            else if (field == 2) proofs.Add(ParseProof(payload));
            // field 3 (ecdsa) is not used by the store.
        }
        if (signed is null || signed.Length == 0) throw new InvalidDataException("The package has no signed header.");
        if (proofs.Count == 0) throw new InvalidDataException("The package has no RSA signature.");
        return (signed, proofs);
    }

    static (byte[] Key, byte[] Sig) ParseProof(ReadOnlySpan<byte> data)
    {
        byte[] key = Array.Empty<byte>(), sig = Array.Empty<byte>();
        int i = 0;
        while (i < data.Length)
        {
            ulong tag = ReadVarint(data, ref i);
            int field = (int)(tag >> 3);
            int wire = (int)(tag & 7);
            if (wire != 2) { Skip(data, ref i, wire); continue; }
            var payload = Field(data, ref i).ToArray();
            if (field == 1) key = payload;
            else if (field == 2) sig = payload;
        }
        if (key.Length == 0 || sig.Length == 0) throw new InvalidDataException("A signature proof is incomplete.");
        return (key, sig);
    }

    static void Verify(ReadOnlySpan<byte> zip, byte[] signedHeader, List<(byte[] Key, byte[] Sig)> proofs, string declared)
    {
        var payload = new byte[Context.Length + 4 + signedHeader.Length + zip.Length];
        Context.CopyTo(payload, 0);
        BitConverter.GetBytes((uint)signedHeader.Length).CopyTo(payload, Context.Length);
        signedHeader.CopyTo(payload, Context.Length + 4);
        zip.CopyTo(payload.AsSpan(Context.Length + 4 + signedHeader.Length));

        foreach (var (key, sig) in proofs)
        {
            // The key itself names the extension; it must be the id in the
            // signed header before its signature is even tried.
            if (!string.Equals(IdFromKey(key), declared, StringComparison.Ordinal)) continue;
            try
            {
                using var rsa = RSA.Create();
                rsa.ImportSubjectPublicKeyInfo(key, out _);
                if (rsa.VerifyData(payload, sig, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)) return;
            }
            catch (CryptographicException) { }
        }
        throw new InvalidDataException("The package's signature does not verify.");
    }

    /// The id in SignedData (field 1): 16 bytes, two nibbles each, written as
    /// the letters a-p — the same shape as the id derived from the key.
    static string CrxId(ReadOnlySpan<byte> signedHeader)
    {
        int i = 0;
        while (i < signedHeader.Length)
        {
            ulong tag = ReadVarint(signedHeader, ref i);
            int field = (int)(tag >> 3);
            int wire = (int)(tag & 7);
            if (wire != 2) { Skip(signedHeader, ref i, wire); continue; }
            var payload = Field(signedHeader, ref i);
            if (field != 1) continue;
            if (payload.Length != 16)
                throw new InvalidDataException("The signed header's id has the wrong length.");
            var text = new StringBuilder(32);
            foreach (var b in payload)
            {
                text.Append((char)('a' + (b >> 4)));
                text.Append((char)('a' + (b & 0x0F)));
            }
            return text.ToString();
        }
        throw new InvalidDataException("The signed header has no extension id.");
    }

    /// Chrome's id: SHA-256 of the public key, first 16 bytes, each nibble
    /// written as a letter a-p.
    static string IdFromKey(byte[] key)
    {
        var hash = SHA256.HashData(key);
        var id = new StringBuilder(32);
        foreach (var b in hash.AsSpan(0, 16))
        {
            id.Append((char)('a' + (b >> 4)));
            id.Append((char)('a' + (b & 0x0F)));
        }
        return id.ToString();
    }

    // ---------------------------------------------------------------- unpack

    static void UnpackAtomic(ReadOnlySpan<byte> zip, string folder)
    {
        var full = Path.GetFullPath(folder);
        var parent = Path.GetDirectoryName(full) ?? throw new InvalidDataException("Invalid extension folder.");
        Directory.CreateDirectory(parent);
        var temp = full + ".new-" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            Directory.CreateDirectory(temp);
            using (var archive = new ZipArchive(new MemoryStream(zip.ToArray()), ZipArchiveMode.Read))
                Extract(archive, temp);
            // The store's own signature file isn't part of an unpacked load.
            var meta = Path.Combine(temp, "_metadata");
            if (Directory.Exists(meta)) Directory.Delete(meta, true);
            if (!File.Exists(Path.Combine(temp, "manifest.json")))
                throw new InvalidDataException("The package has no manifest.json.");
            if (Directory.Exists(full)) Directory.Delete(full, true);
            Directory.Move(temp, full);
        }
        finally
        {
            try { if (Directory.Exists(temp)) Directory.Delete(temp, true); } catch { }
        }
    }

    static void Extract(ZipArchive archive, string folder)
    {
        var root = Path.GetFullPath(folder) + Path.DirectorySeparatorChar;
        long total = 0;
        foreach (var entry in archive.Entries)
        {
            total += entry.Length;
            if (total > 200L * 1024 * 1024) throw new InvalidDataException("The package is too large.");
            var target = Path.GetFullPath(Path.Combine(folder, entry.FullName));
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The package escapes its folder.");
            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
            {
                Directory.CreateDirectory(target);
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, true);
        }
    }

    // ---------------------------------------------------------------- protobuf

    static ulong ReadVarint(ReadOnlySpan<byte> data, ref int i)
    {
        ulong value = 0;
        int shift = 0;
        while (true)
        {
            if (i >= data.Length || shift > 63) throw new InvalidDataException("Malformed CRX header.");
            byte b = data[i++];
            value |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) return value;
            shift += 7;
        }
    }

    static ReadOnlySpan<byte> Field(ReadOnlySpan<byte> data, ref int i)
    {
        ulong len = ReadVarint(data, ref i);
        if (len > int.MaxValue || i + (int)len > data.Length) throw new InvalidDataException("Malformed CRX header.");
        var payload = data.Slice(i, (int)len);
        i += (int)len;
        return payload;
    }

    static void Skip(ReadOnlySpan<byte> data, ref int i, int wire)
    {
        switch (wire)
        {
            case 0: ReadVarint(data, ref i); break;
            case 1: i += 8; break;
            case 5: i += 4; break;
            default: throw new InvalidDataException("Malformed CRX header.");
        }
        if (i > data.Length) throw new InvalidDataException("Malformed CRX header.");
    }
}
