namespace AERIE;

public sealed class InstalledExtension
{
    public string Name { get; set; } = "";
    public string Folder { get; set; } = "";
    public string? StoreId { get; set; }
    public bool Enabled { get; set; } = true;
}

public sealed class Extensions
{
    public List<InstalledExtension> Items { get; set; } = new();
}
