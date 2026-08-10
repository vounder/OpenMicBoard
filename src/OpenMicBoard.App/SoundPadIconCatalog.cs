namespace OpenMicBoard.App;

/// <summary>Locally defined, stable Fluent glyph choices for sound pads.</summary>
public sealed record SoundPadIcon(string Token, string DisplayName, string Glyph, string Keywords);

public static class SoundPadIconCatalog
{
    public const string DefaultToken = "Audio";

    public static IReadOnlyList<SoundPadIcon> All { get; } =
    [
        new("Audio", "Audio", "\uE8D6", "sound music speaker"), new("Music", "Music", "\uE8D6", "song note"),
        new("Volume", "Volume", "\uE767", "loud speaker"), new("Microphone", "Microphone", "\uE720", "mic voice"),
        new("Play", "Play", "\uE768", "start cue"), new("Pause", "Pause", "\uE769", "hold"),
        new("Stop", "Stop", "\uE71A", "end"), new("Record", "Record", "\uE7C8", "recording"),
        new("Headphones", "Headphones", "\uE7F6", "headset preview"), new("Radio", "Radio", "\uE9CE", "broadcast"),
        new("Bell", "Bell", "\uEA8F", "alert notification"), new("Alarm", "Alarm", "\uE7ED", "timer clock"),
        new("Emoji", "Emoji", "\uE899", "reaction laugh"), new("Heart", "Heart", "\uEB51", "love"),
        new("Star", "Star", "\uE734", "favorite"), new("Fire", "Fire", "\uE7E6", "hype flame"),
        new("Lightning", "Lightning", "\uE945", "electric zap"), new("Party", "Party", "\uECA6", "celebrate confetti"),
        new("Laugh", "Laugh", "\uE11D", "funny comedy"), new("Sad", "Sad", "\uE11E", "cry emotion"),
        new("ThumbsUp", "Thumbs up", "\uE8E1", "like approval"), new("ThumbsDown", "Thumbs down", "\uE8E0", "dislike"),
        new("Clap", "Clap", "\uE8B6", "applause hands"), new("Trophy", "Trophy", "\uE7FC", "win award"),
        new("Game", "Game", "\uE7FC", "gaming controller"), new("Camera", "Camera", "\uE722", "photo shutter"),
        new("Video", "Video", "\uE714", "movie clip"), new("Chat", "Chat", "\uE8BD", "talk speech"),
        new("Mail", "Mail", "\uE715", "message"), new("Phone", "Phone", "\uE717", "call ring"),
        new("Person", "Person", "\uE77B", "user voice"), new("People", "People", "\uE716", "crowd audience"),
        new("Robot", "Robot", "\uE99A", "bot"), new("Ghost", "Ghost", "\uE619", "spooky"),
        new("Cat", "Cat", "\uE9D6", "animal meow"), new("Dog", "Dog", "\uE5C3", "animal bark"),
        new("Car", "Car", "\uE804", "vehicle horn"), new("Plane", "Plane", "\uE709", "aircraft"),
        new("Rocket", "Rocket", "\uE90F", "launch space"), new("Home", "Home", "\uE80F", "house"),
        new("Coffee", "Coffee", "\uEAEF", "drink"), new("Food", "Food", "\uE26B", "eat"),
        new("Gift", "Gift", "\uE8F7", "present"), new("Flag", "Flag", "\uE7C1", "marker"),
        new("Shield", "Shield", "\uEA18", "safety"), new("Warning", "Warning", "\uEA39", "alert error"),
        new("Info", "Info", "\uE946", "information"), new("Question", "Question", "\uE897", "help"),
        new("Settings", "Settings", "\uE713", "gear"), new("Folder", "Folder", "\uE8B7", "files"),
        new("Clock", "Clock", "\uE823", "time"), new("Check", "Check", "\uE73E", "success done"),
        new("Pin", "Pin", "\uE718", "marker location"), new("Key", "Key", "\uE72E", "shortcut hotkey")
    ];

    public static SoundPadIcon Resolve(string? token) => All.FirstOrDefault(x =>
        string.Equals(x.Token, token, StringComparison.OrdinalIgnoreCase)) ?? All[0];

    public static IEnumerable<SoundPadIcon> Search(string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return All;
        return All.Where(x => ($"{x.Token} {x.DisplayName} {x.Keywords}").Contains(query.Trim(), StringComparison.OrdinalIgnoreCase));
    }
}
