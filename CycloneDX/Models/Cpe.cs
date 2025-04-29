namespace CycloneDX.Models;

public record Cpe(
    string? Part = null,
    string? Vendor = null,
    string? Product = null,
    string? Version = null,
    string? Update = null,
    string? Edition = null,
    string? Language = null,
    string? SoftwareEdition = null,
    string? TargetSoftware = null,
    string? TargetHardware = null,
    string? Other = null)
{
    public override string ToString() =>
        $"cpe:2.3:{Format(Part)}:{Format(Vendor)}:{Format(Product)}:{Format(Version)}:" +
        $"{Format(Update)}:{Format(Edition)}:{Format(Language)}:{Format(SoftwareEdition)}:" +
        $"{Format(TargetSoftware)}:{Format(TargetHardware)}:{Format(Other)}";

    private static string Format(string? value) => value ?? "*";

    public static implicit operator string(Cpe cpe) => cpe.ToString();

    public static Cpe Create(
        string? part = null,
        string? vendor = null,
        string? product = null,
        string? version = null,
        string? update = null,
        string? edition = null,
        string? language = null,
        string? softwareEdition = null,
        string? targetSoftware = null,
        string? targetHardware = null,
        string? other = null) =>
        new Cpe(part, vendor, product, version, update, edition, language, softwareEdition, targetSoftware, targetHardware, other);
}
