namespace Enterprise.Gpt.Integration.Test.TestInfrastructure.FileAgentSpike;

/// <summary>
/// The sandbox image as it stood on the day of the run.
/// </summary>
/// <param name="RecordedUtc">When the probe's result was read back.</param>
/// <param name="PythonVersion">The interpreter version, which dates the image as well as anything.</param>
/// <param name="Libraries">Every library the probe tried to import, importable or not.</param>
/// <param name="OfficeConverter">The <c>soffice</c>/<c>libreoffice</c> binary, if any. Its presence
/// promotes every Office-to-PDF cell from structural to faithful.</param>
internal sealed record SandboxInventory(
    DateTimeOffset RecordedUtc,
    string PythonVersion,
    IReadOnlyList<InventoryLibrary> Libraries,
    OfficeConverterProbe OfficeConverter);
