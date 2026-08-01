namespace Ut2004Stats.Core.Parsing;

/// <summary>Why a match ended. Only matches that reached a real conclusion are imported.</summary>
public enum ParsedEndReason
{
    None = 0,
    FragLimit,
    TimeLimit,
    TeamScoreLimit,
    GoalScoreLimit,
    RoundLimit,
    LastMan,
    Draw,
    Artifacts,
    MapChange,
    ServerQuit,
    EndWarmup,
    Unknown,
}

/// <summary>Player IDs below zero are sentinels rather than real slots.</summary>
public static class PlayerSentinel
{
    /// <summary>No/invalid actor — the world.</summary>
    public const int World = -1;
    /// <summary>Environmental killer (assigned by the parser, not present in logs).</summary>
    public const int Environment = -2;
    /// <summary>An Invasion monster.</summary>
    public const int Monster = -3;
    /// <summary>Referenced a slot that never connected — such lines are dropped.</summary>
    public const int Unlogged = -99;
}

public class ParsedPlayer
{
    public int SlotId { get; set; }
    public string Name { get; set; } = "";
    public int Team { get; set; } = 255;
    public bool IsBot { get; set; }

    public int Frags { get; set; }
    public int Deaths { get; set; }
    public int Suicides { get; set; }
    public int TeamKills { get; set; }
    public double Score { get; set; }

    public double ConnectTime { get; set; }
    public double DisconnectTime { get; set; } = -1;
    public double TimePlayed { get; set; }

    public int FlagsTaken { get; set; }
    public int FlagsCaptured { get; set; }
    public int FlagsReturned { get; set; }
    public int FlagsDropped { get; set; }

    public bool FirstBlood { get; set; }
    public int Headshots { get; set; }

    /// <summary>Highest multi-kill tier reached (1 = Double Kill ... 7 = Holy Shit).</summary>
    public int BestMultiKill { get; set; }
    /// <summary>Highest spree tier reached (1 = Killing Spree ... 6 = Wicked Sick).</summary>
    public int BestSpree { get; set; }

    /// <summary>Running count of kills since last death, used to derive sprees.</summary>
    internal int CurrentSpreeKills { get; set; }

    public Dictionary<string, int> WeaponKills { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, int> WeaponDeaths { get; } = new(StringComparer.OrdinalIgnoreCase);
}

public class ParsedKill
{
    public double Time { get; set; }
    public int KillerSlot { get; set; }
    public int VictimSlot { get; set; }
    public string DamageType { get; set; } = "";
    public bool IsSuicide { get; set; }
    public bool IsTeamKill { get; set; }
}

public enum ParsedSpecialKind { MultiKill, KillingSpree, FirstBlood }

public class ParsedSpecial
{
    public double Time { get; set; }
    public int SlotId { get; set; }
    public ParsedSpecialKind Kind { get; set; }
    public int Level { get; set; }
}

/// <summary>The full result of reading one stats log file.</summary>
public class ParsedMatch
{
    public string LogFileName { get; set; } = "";

    public bool SawNewGame { get; set; }
    public bool SawStartGame { get; set; }
    public ParsedEndReason EndReason { get; set; } = ParsedEndReason.None;

    public DateTime MatchDate { get; set; }
    public string MapName { get; set; } = "";
    public string MapTitle { get; set; } = "";
    public string GameTypeName { get; set; } = "";
    public string GameTypeClass { get; set; } = "";
    public string Mutators { get; set; } = "";

    public string ServerName { get; set; } = "";
    public int ServerPort { get; set; }
    public int TimeLimit { get; set; }
    public int GoalScore { get; set; }

    public double StartTime { get; set; }
    public double EndTime { get; set; }
    public double DurationSeconds => Math.Max(0, EndTime - StartTime);

    public double[] TeamScores { get; } = new double[4];

    public Dictionary<int, ParsedPlayer> Players { get; } = [];
    public List<ParsedKill> Kills { get; } = [];
    public List<ParsedSpecial> Specials { get; } = [];

    /// <summary>
    /// A match is worth importing when it started properly and produced a result.
    /// </summary>
    /// <remarks>
    /// <para>Map changes count. On a server with map voting most games end that way,
    /// and the stats accumulated up to the switch are real — excluding them would
    /// throw away the majority of play. This matches UTStatsDB's default.</para>
    /// <para>Warm-up rounds and server shutdowns are still excluded: the first is
    /// not a real game, and the second usually means the log was cut short.</para>
    /// </remarks>
    public bool IsComplete =>
        SawNewGame
        && SawStartGame
        && EndReason is ParsedEndReason.FragLimit
            or ParsedEndReason.TimeLimit
            or ParsedEndReason.TeamScoreLimit
            or ParsedEndReason.GoalScoreLimit
            or ParsedEndReason.RoundLimit
            or ParsedEndReason.LastMan
            or ParsedEndReason.Draw
            or ParsedEndReason.Artifacts
            or ParsedEndReason.MapChange
        && Players.Count > 0;
}
