namespace Ut2004Stats.Core.Domain;

/// <summary>A UT2004 dedicated server that produced match logs.</summary>
public class Server
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public int Port { get; set; }
    public DateTime FirstSeen { get; set; }
    public DateTime LastSeen { get; set; }

    public List<Match> Matches { get; set; } = [];
}

/// <summary>A map, keyed by its in-game package name (e.g. <c>DM-Rankin</c>).</summary>
public class GameMap
{
    public int Id { get; set; }
    public string Name { get; set; } = "";

    /// <summary>Map name prefix (DM, CTF, ONS, ...) derived from <see cref="Name"/>.</summary>
    public string Prefix { get; set; } = "";

    public List<Match> Matches { get; set; } = [];
}

/// <summary>A gametype, keyed by its UnrealScript class (e.g. <c>XGame.xDeathMatch</c>).</summary>
public class GameType
{
    public int Id { get; set; }
    public string ClassName { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public bool IsTeamGame { get; set; }

    public List<Match> Matches { get; set; } = [];
}

/// <summary>
/// A player identity, de-duplicated by name across matches. UT2004 logs carry no
/// persistent account id, so the name is the only stable key available.
/// </summary>
public class Player
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public bool IsBot { get; set; }
    public DateTime FirstSeen { get; set; }
    public DateTime LastSeen { get; set; }

    public List<MatchPlayer> Appearances { get; set; } = [];
}

public class Weapon
{
    public int Id { get; set; }
    public string ClassName { get; set; } = "";
    public string DisplayName { get; set; } = "";
}

/// <summary>How a match came to an end. Only completed matches are imported.</summary>
public enum MatchEndReason
{
    Unknown = 0,
    TimeLimit = 1,
    FragLimit = 2,
    ScoreLimit = 3,
    Other = 4,
}

/// <summary>A single completed game.</summary>
public class Match
{
    public int Id { get; set; }

    public int ServerId { get; set; }
    public Server Server { get; set; } = null!;

    public int MapId { get; set; }
    public GameMap Map { get; set; } = null!;

    public int GameTypeId { get; set; }
    public GameType GameType { get; set; } = null!;

    public DateTime StartedAt { get; set; }

    /// <summary>Match length in seconds, taken from the final log timestamp.</summary>
    public double DurationSeconds { get; set; }

    public int TimeLimit { get; set; }
    public int GoalScore { get; set; }
    public MatchEndReason EndReason { get; set; }

    /// <summary>Red team score. Only meaningful when <see cref="GameType"/> is a team game.</summary>
    public int RedScore { get; set; }
    public int BlueScore { get; set; }

    /// <summary>Mutators active for this match, comma separated. Empty when none.</summary>
    public string Mutators { get; set; } = "";

    /// <summary>Source log file name — also the idempotency key for imports.</summary>
    public string LogFileName { get; set; } = "";
    public DateTime ImportedAt { get; set; }

    public List<MatchPlayer> Players { get; set; } = [];
    public List<KillEvent> Kills { get; set; } = [];
    public List<SpecialEvent> SpecialEvents { get; set; } = [];
}

/// <summary>Team assignment. UT2004 uses 255 for "no team" in non-team games.</summary>
public static class Teams
{
    public const int Red = 0;
    public const int Blue = 1;
    public const int None = 255;
}

/// <summary>One player's participation and per-match totals.</summary>
public class MatchPlayer
{
    public int Id { get; set; }

    public int MatchId { get; set; }
    public Match Match { get; set; } = null!;

    public int PlayerId { get; set; }
    public Player Player { get; set; } = null!;

    /// <summary>The in-log slot id for this player within this match.</summary>
    public int SlotId { get; set; }

    /// <summary>Name as used in this specific match (players can rename between games).</summary>
    public string NameUsed { get; set; } = "";

    public int Team { get; set; } = Teams.None;
    public bool IsBot { get; set; }

    public int Score { get; set; }
    public int Frags { get; set; }
    public int Deaths { get; set; }
    public int Suicides { get; set; }
    public int TeamKills { get; set; }

    /// <summary>Seconds spent in the match.</summary>
    public double TimePlayed { get; set; }

    // Flag / objective stats — zero for gametypes that don't use them.
    public int FlagsTaken { get; set; }
    public int FlagsCaptured { get; set; }
    public int FlagsReturned { get; set; }
    public int FlagKills { get; set; }

    // Special events
    public int FirstBloods { get; set; }
    public int Headshots { get; set; }
    public int BestSpree { get; set; }
    public int BestMultiKill { get; set; }

    public List<MatchPlayerWeapon> Weapons { get; set; } = [];

    /// <summary>Kills per death, guarding against divide-by-zero.</summary>
    public double Ratio => Deaths == 0 ? Frags : Math.Round((double)Frags / Deaths, 2);

    /// <summary>Share of engagements won, as a percentage.</summary>
    public double Efficiency =>
        Frags + Deaths == 0 ? 0 : Math.Round(100.0 * Frags / (Frags + Deaths), 1);
}

/// <summary>Per-weapon totals for one player in one match.</summary>
public class MatchPlayerWeapon
{
    public int Id { get; set; }

    public int MatchPlayerId { get; set; }
    public MatchPlayer MatchPlayer { get; set; } = null!;

    public int WeaponId { get; set; }
    public Weapon Weapon { get; set; } = null!;

    public int Kills { get; set; }
    public int Deaths { get; set; }
}

/// <summary>An individual frag, retained so kill matrices and timelines can be rebuilt.</summary>
public class KillEvent
{
    public long Id { get; set; }

    public int MatchId { get; set; }
    public Match Match { get; set; } = null!;

    /// <summary>Seconds from match start.</summary>
    public double Time { get; set; }

    /// <summary>Null for environmental deaths with no attributable killer.</summary>
    public int? KillerMatchPlayerId { get; set; }
    public MatchPlayer? KillerMatchPlayer { get; set; }

    public int VictimMatchPlayerId { get; set; }
    public MatchPlayer VictimMatchPlayer { get; set; } = null!;

    public int? WeaponId { get; set; }
    public Weapon? Weapon { get; set; }

    public bool IsSuicide { get; set; }
    public bool IsTeamKill { get; set; }
}

public enum SpecialEventKind
{
    MultiKill = 0,
    KillingSpree = 1,
    FirstBlood = 2,
}

/// <summary>Multi-kills, sprees and first blood, kept as discrete events for timelines.</summary>
public class SpecialEvent
{
    public long Id { get; set; }

    public int MatchId { get; set; }
    public Match Match { get; set; } = null!;

    public int MatchPlayerId { get; set; }
    public MatchPlayer MatchPlayer { get; set; } = null!;

    public double Time { get; set; }
    public SpecialEventKind Kind { get; set; }

    /// <summary>Tier of the event — e.g. 2 = Double Kill, 3 = Multi Kill; 1 = Killing Spree, 2 = Rampage.</summary>
    public int Level { get; set; }
}
