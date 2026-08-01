using System.Text.RegularExpressions;

namespace Ut2004Stats.Core.Parsing;

/// <summary>
/// Lookups that turn raw UnrealScript class names from the logs into presentable
/// names. Anything unknown falls back to a humanised form of the class name, so a
/// mod or bonus-pack weapon still displays sensibly rather than breaking the page.
/// </summary>
public static partial class GameCatalog
{
    private sealed record GameTypeInfo(string DisplayName, bool IsTeamGame);

    /// <summary>
    /// Keyed by the game <em>name</em> as it appears in the log's NG line, and also
    /// by the gametype class, since either may be the more reliable identifier.
    /// </summary>
    private static readonly Dictionary<string, GameTypeInfo> GameTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        // Class names
        ["XGame.xDeathMatch"] = new("Deathmatch", false),
        ["XGame.xTeamGame"] = new("Team Deathmatch", true),
        ["XGame.xCTFGame"] = new("Capture the Flag", true),
        ["XGame.xVehicleCTFGame"] = new("Vehicle CTF", true),
        ["XGame.InstagibCTF"] = new("Instagib CTF", true),
        ["XGame.xDoubleDom"] = new("Double Domination", true),
        ["XGame.xBombingRun"] = new("Bombing Run", true),
        ["UT2k4Assault.ASGameInfo"] = new("Assault", true),
        ["Onslaught.ONSOnslaughtGame"] = new("Onslaught", true),
        ["SkaarjPack.Invasion"] = new("Invasion", true),
        ["BonusPack.xLastManStandingGame"] = new("Last Man Standing", false),
        ["BonusPack.xMutantGame"] = new("Mutant", false),

        // Game names as logged
        ["Deathmatch"] = new("Deathmatch", false),
        ["Team Deathmatch"] = new("Team Deathmatch", true),
        ["Capture the Flag"] = new("Capture the Flag", true),
        ["Vehicle CTF"] = new("Vehicle CTF", true),
        ["Instagib CTF"] = new("Instagib CTF", true),
        ["Double Domination"] = new("Double Domination", true),
        ["Domination"] = new("Double Domination", true),
        ["Bombing Run"] = new("Bombing Run", true),
        ["Assault"] = new("Assault", true),
        ["Onslaught"] = new("Onslaught", true),
        ["Invasion"] = new("Invasion", true),
        ["Last Man Standing"] = new("Last Man Standing", false),
        ["Mutant"] = new("Mutant", false),
        ["CTF4"] = new("Capture the Flag", true),
    };

    /// <summary>Gametypes whose scoring is team-based, used to decide UI treatment.</summary>
    public static bool IsTeamGame(string identifier) =>
        GameTypes.TryGetValue(identifier ?? "", out var info)
            ? info.IsTeamGame
            // Unknown gametype: infer from the name as a best effort.
            : identifier is not null
              && (identifier.Contains("team", StringComparison.OrdinalIgnoreCase)
                  || identifier.Contains("CTF", StringComparison.OrdinalIgnoreCase)
                  || identifier.Contains("Onslaught", StringComparison.OrdinalIgnoreCase)
                  || identifier.Contains("Assault", StringComparison.OrdinalIgnoreCase)
                  || identifier.Contains("Domination", StringComparison.OrdinalIgnoreCase));

    public static string GameTypeName(string identifier) =>
        GameTypes.TryGetValue(identifier ?? "", out var info)
            ? info.DisplayName
            : Humanise(ShortName(identifier ?? ""));

    /// <summary>
    /// Weapon and damage-type classes mapped to display names. Both the weapon class
    /// (e.g. <c>FlakCannon</c>) and the damage types it produces (<c>DamTypeFlakChunk</c>,
    /// <c>DamTypeFlakShell</c>) resolve to the same weapon, so kills group correctly
    /// regardless of which fire mode landed the shot.
    /// </summary>
    private static readonly Dictionary<string, string> Weapons = new(StringComparer.OrdinalIgnoreCase)
    {
        ["None"] = "None",

        ["TransLauncher"] = "Translocator",
        ["DamTypeTeleFrag"] = "Translocator",
        ["DamTypeTelefragged"] = "Telefrag",

        ["ShieldGun"] = "Shield Gun",
        ["DamTypeShieldImpact"] = "Shield Gun",

        ["AssaultRifle"] = "Assault Rifle",
        ["DamTypeAssaultBullet"] = "Assault Rifle",
        ["DamTypeAssaultGrenade"] = "Assault Rifle",

        ["LinkGun"] = "Link Gun",
        ["DamTypeLinkPlasma"] = "Link Gun",
        ["DamTypeLinkShaft"] = "Link Gun",

        ["ShockRifle"] = "Shock Rifle",
        ["DamTypeShockBeam"] = "Shock Rifle",
        ["DamTypeShockBall"] = "Shock Rifle",
        ["DamTypeShockCombo"] = "Shock Rifle",

        ["BioRifle"] = "Bio Rifle",
        ["DamTypeBioGlob"] = "Bio Rifle",

        ["Minigun"] = "Minigun",
        ["DamTypeMinigunBullet"] = "Minigun",
        ["DamTypeMinigunAlt"] = "Minigun",

        ["FlakCannon"] = "Flak Cannon",
        ["DamTypeFlakChunk"] = "Flak Cannon",
        ["DamTypeFlakShell"] = "Flak Cannon",
        ["FlakDeath"] = "Flak Cannon",

        ["RocketLauncher"] = "Rocket Launcher",
        ["DamTypeRocket"] = "Rocket Launcher",
        ["DamTypeRocketHoming"] = "Rocket Launcher",
        ["RocketDeath"] = "Rocket Launcher",

        ["LightningGun"] = "Lightning Gun",
        ["SniperRifle"] = "Lightning Gun",
        ["DamTypeSniperShot"] = "Lightning Gun",
        ["DamTypeSniperHeadShot"] = "Lightning Gun",

        ["ClassicSniperRifle"] = "Sniper Rifle",
        ["DamTypeClassicSniper"] = "Sniper Rifle",
        ["DamTypeClassicHeadshot"] = "Sniper Rifle",

        ["Redeemer"] = "Redeemer",
        ["DamTypeRedeemer"] = "Redeemer",
        ["RedeemerDeath"] = "Redeemer",

        ["SuperShockRifle"] = "Instagib Rifle",
        ["DamSuperShockRifle"] = "Instagib Rifle",
        ["DamTypeSuperShockBeam"] = "Instagib Rifle",
        ["DamZoomSuperShockRifle"] = "Instagib Rifle",
        ["ZoomSuperShockBeamDamage"] = "Instagib Rifle",
        ["ZoomSuperShockRifle"] = "Instagib Rifle",
        ["DamTypeInstaVape"] = "Instagib Rifle",

        ["Painter"] = "Ion Painter",
        ["DamTypeIonBlast"] = "Ion Cannon",
        ["DamTypeIonVolume"] = "Orbital Ion Cannon",
        ["ONSPainter"] = "Target Painter",

        ["ONSAVRiL"] = "AVRiL",
        ["DamTypeONSAVRiLRocket"] = "AVRiL",
        ["ONSMineLayer"] = "Mine Layer",
        ["DamTypeONSMine"] = "Mine Layer",
        ["ONSGrenadeLauncher"] = "Grenade Launcher",
        ["DamTypeONSGrenade"] = "Grenade Launcher",

        ["BallLauncher"] = "Ball Launcher",
        ["DamBallLauncher"] = "Ball Launcher",

        ["Ripper"] = "Ripper",
        ["DamTypeRipper"] = "Ripper",
        ["DamTypeRipperHeadshot"] = "Ripper",
        ["RipperAltDeath"] = "Ripper",

        // Environmental and non-weapon damage sources.
        ["fell"] = "Falling",
        ["FellLava"] = "Lava",
        ["Crushed"] = "Crushed",
        ["Suicided"] = "Suicide",
        ["Gibbed"] = "Gibbed",
        ["Drowned"] = "Drowning",
        ["Corroded"] = "Slime",
        ["SwamTooFar"] = "Drowning",
        ["Depressurized"] = "Vacuum",
        ["shredded"] = "Shredded",
        ["jolted"] = "Electrocuted",
        ["impact"] = "Impact",
        ["exploded"] = "Explosion",
        ["shot"] = "Gunfire",
        ["Burned"] = "Fire",
        ["ConvoyGibbed"] = "Convoy",
        ["MeleeDamage"] = "Melee",
        ["DamTypeExploBarrel"] = "Exploding Barrel",
        ["DamageType"] = "Unknown",
        ["TeamChange"] = "Team Change",

        // Vehicles and turrets.
        ["DamTypeONSVehicle"] = "Vehicle",
        ["DamTypeONSVehicleExplosion"] = "Vehicle Explosion",
        ["DamTypeDestroyedVehicleRoadKill"] = "Road Kill",
        ["ONSAttackCraft"] = "Raptor",
        ["DamTypeAttackCraftPlasma"] = "Raptor",
        ["DamTypeAttackCraftMissle"] = "Raptor",
        ["ONSHoverBike"] = "Manta",
        ["DamTypeHoverBikePlasma"] = "Manta",
        ["ONSRV"] = "Scorpion",
        ["DamTypeONSWeb"] = "Scorpion",
        ["DamTypeONSRVBlade"] = "Scorpion",
        ["ONSPRV"] = "Hellbender",
        ["DamTypePRVLaser"] = "Hellbender",
        ["DamTypePRVCombo"] = "Hellbender",
        ["DamTypeSkyMine"] = "Hellbender",
        ["ONSHoverTank"] = "Goliath",
        ["DamTypeTankShell"] = "Goliath",
        ["DamTypeONSChainGun"] = "Goliath",
        ["ONSMobileAssaultStation"] = "Leviathan",
        ["DamTypeMASRocket"] = "Leviathan",
        ["DamTypeMASCannon"] = "Leviathan",
        ["DamTypeMASPlasma"] = "Leviathan",
        ["ONSShockTank"] = "Paladin",
        ["DamTypeShockTankShockBall"] = "Paladin",
        ["ONSArtillery"] = "SPMA",
        ["DamTypeArtilleryShell"] = "SPMA",
        ["ONSDualAttackCraft"] = "Cicada",
        ["DamTypeONSCicadaRocket"] = "Cicada",
        ["DamTypeONSCicadaLaser"] = "Cicada",
        ["ONSBomber"] = "Dragonfly",
        ["DamTypeIonTankBlast"] = "Ion Plasma Tank",
        ["DamTypeSentinelLaser"] = "Sentinel",
        ["DamTypeIonCannonBlast"] = "Ion Cannon",
        ["DamTypeTurretBeam"] = "Turret",
        ["DamTypeMinigunTurretBullet"] = "Minigun Turret",
        ["DamTypeLinkTurretPlasma"] = "Link Turret",
        ["DamTypeLinkTurretBeam"] = "Link Turret",
    };

    /// <summary>
    /// Maps a weapon or damage-type class to a display name. Unknown values are
    /// unwrapped from their damage-type decoration and humanised, so mod content
    /// still reads sensibly.
    /// </summary>
    public static string WeaponName(string className)
    {
        if (string.IsNullOrWhiteSpace(className)) return "Unknown";

        var name = StripModAffixes(ShortName(className));
        if (Weapons.TryGetValue(name, out var known)) return known;

        // Unwrap damage-type decoration and retry — longest affix first so that
        // "DamType..." is not mistakenly reduced by the shorter "Dam" prefix.
        var stripped = DamageAffix().Replace(name, "");
        if (stripped.Length > 0 && Weapons.TryGetValue(stripped, out var viaDamage)) return viaDamage;

        return Humanise(stripped.Length > 0 ? stripped : name);
    }

    /// <summary>
    /// True when a damage type represents a headshot. There is no dedicated headshot
    /// event in UT2004 logs, so the damage type is the only available signal.
    /// </summary>
    public static bool IsHeadshot(string damageType) =>
        !string.IsNullOrEmpty(damageType)
        && damageType.Contains("HeadShot", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Removes the decoration popular server mods add to weapon and damage-type
    /// classes, so a UTComp server's <c>BS_DamTypeRocket</c> still resolves to
    /// the Rocket Launcher rather than being treated as unknown mod content.
    /// </summary>
    private static string StripModAffixes(string name)
    {
        if (string.IsNullOrEmpty(name)) return name;

        // Prefixes, longest first so a shorter one cannot claim the match.
        string[] prefixes = ["ut2vweap", "OLTeams", "NewNet_", "BS_"];
        foreach (var prefix in prefixes)
        {
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && name.Length > prefix.Length)
                return name[prefix.Length..];
        }

        // Team ArenaMaster suffixes its classes instead.
        const string suffix = "_3SPN";
        if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) && name.Length > suffix.Length)
            return name[..^suffix.Length];

        return name;
    }

    /// <summary>Takes the class portion of a <c>Package.Class</c> reference.</summary>
    public static string ShortName(string className)
    {
        if (string.IsNullOrEmpty(className)) return "";
        var idx = className.LastIndexOf('.');
        return idx >= 0 && idx < className.Length - 1 ? className[(idx + 1)..] : className;
    }

    /// <summary>Derives the map prefix (DM, CTF, ONS...) from a map name.</summary>
    public static string MapPrefix(string mapName)
    {
        if (string.IsNullOrEmpty(mapName)) return "";
        var idx = mapName.IndexOf('-');
        return idx > 0 ? mapName[..idx].ToUpperInvariant() : "";
    }

    /// <summary>Splits PascalCase into spaced words so unknown classes still read well.</summary>
    private static string Humanise(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "Unknown";
        var spaced = PascalBoundary().Replace(value, " $1").Trim();
        return spaced.Length == 0 ? value : spaced;
    }

    // Ordered longest-first: DamType must win over Dam.
    [GeneratedRegex(@"^(DamageType|DamType|DmgType|Damage|Dam)|(DamageType|Damage|Death|DamType)$")]
    private static partial Regex DamageAffix();

    [GeneratedRegex(@"(?<!^)([A-Z][a-z]|(?<=[a-z])[A-Z0-9])")]
    private static partial Regex PascalBoundary();
}
