namespace Crest.Services;

/// <summary>What every closed-island widget shares: where it sits, its colour and its text size.</summary>
public class IdleItem
{
    /// <summary>"Off", "Left" or "Right".</summary>
    public string Side { get; set; } = "Off";
    public string Color { get; set; } = "#FFFFFF";
    public double Size { get; set; } = 12;
}

public sealed class ClockItem : IdleItem
{
    public bool H24 { get; set; }
    public bool Seconds { get; set; }
    public bool AmPm { get; set; }
    /// <summary>The colon blinks once a second.</summary>
    public bool Blink { get; set; }
}

public sealed class DateItem : IdleItem
{
    /// <summary>"Sun 22", "22 Nov", "Nov 22", "Sunday" or "22/11".</summary>
    public string Format { get; set; } = "Sun 22";
    public bool Upper { get; set; }
}

public sealed class BatteryItem : IdleItem
{
    /// <summary>"Icon + %", "%" or "Icon".</summary>
    public string Style { get; set; } = "Icon + %";
    /// <summary>Only shows at or below this charge. 100 = always.</summary>
    public int Below { get; set; } = 100;
    public bool LevelColors { get; set; } = true;
    public bool Bolt { get; set; } = true;
}

/// <summary>CPU or memory.</summary>
public sealed class LoadItem : IdleItem
{
    /// <summary>"Text", "Bar" or "Both".</summary>
    public string Style { get; set; } = "Text";
    public bool Label { get; set; } = true;
    public bool LoadColors { get; set; } = true;
    /// <summary>Only shows at or above this load. 0 = always.</summary>
    public int Above { get; set; }
    /// <summary>Memory only: "%" or "GB".</summary>
    public string Unit { get; set; } = "%";
}

public sealed class NetItem : IdleItem
{
    /// <summary>"Down", "Up" or "Both".</summary>
    public string Show { get; set; } = "Down";
    public bool Bits { get; set; }
    public bool Arrows { get; set; } = true;
    /// <summary>Hides while less than 20 KB/s is moving.</summary>
    public bool HideIdle { get; set; }
}

/// <summary>A ring with an arrow that fills with the download or upload speed.</summary>
public sealed class TransferItem : IdleItem
{
    /// <summary>"Download", "Upload" or "Both".</summary>
    public string Show { get; set; } = "Download";
    /// <summary>The speed as text beside the ring.</summary>
    public bool Speed { get; set; }
    /// <summary>Blue for downloads, green for uploads, instead of the widget colour.</summary>
    public bool Tint { get; set; } = true;
    /// <summary>Only shows while something is downloading or uploading.</summary>
    public bool HideIdle { get; set; } = true;
    /// <summary>Real download progress from the Crest Downloads browser extension, when it is installed.</summary>
    public bool Browser { get; set; } = true;
}

public sealed class CountdownItem : IdleItem
{
    public string Label { get; set; } = "Xmas";
    /// <summary>yyyy-MM-dd HH:mm (or anything DateTime can read).</summary>
    public string Target { get; set; } = "2026-12-25 00:00";
    /// <summary>"Auto", "Days" or "Exact".</summary>
    public string Format { get; set; } = "Auto";
    /// <summary>What happens once it is reached: "Count up", "Done" or "Hide".</summary>
    public string After { get; set; } = "Done";
}

public sealed class ProgressItem : IdleItem
{
    /// <summary>"Day", "Work day", "Week", "Month" or "Year".</summary>
    public string Of { get; set; } = "Day";
    /// <summary>"%", "Bar" or "Both".</summary>
    public string Style { get; set; } = "Both";
    public bool Label { get; set; } = true;
    public int WorkStart { get; set; } = 9;
    public int WorkEnd { get; set; } = 17;
}

public sealed class UptimeItem : IdleItem
{
    /// <summary>"PC start" or "Crest start".</summary>
    public string Since { get; set; } = "PC start";
    /// <summary>"1h 23m" or "01:23:45".</summary>
    public string Format { get; set; } = "1h 23m";
    public bool Label { get; set; } = true;
}

public sealed class TextItem : IdleItem
{
    public string Text { get; set; } = "Hello";
    public bool Bold { get; set; }
    public bool Italic { get; set; }
    public int MaxWidth { get; set; } = 140;
}
