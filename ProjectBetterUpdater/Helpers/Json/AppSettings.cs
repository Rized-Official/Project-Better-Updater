using System;

namespace ProjectBetterUpdater.Helpers.Json;

public class AppSettings
{
    public string Version { get; set; } = "no version";

    public DateTimeOffset? LastCheckUtc { get; set; }
}