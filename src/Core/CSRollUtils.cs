using System.Linq;

using SwiftlyS2.Shared;
using SwiftlyS2.Shared.Natives;
using SwiftlyS2.Shared.Players;
using SwiftlyS2.Shared.SchemaDefinitions;

using CSRoll.Modifiers;

namespace CSRoll.Core;

public static class CSRollUtils
{
    /// <summary>Single source of truth for the admin permission string - referenced by command registration (CSRoll.Commands.cs) and by the admin-only chat helpers below.</summary>
    public const string AdminPermission = "gamemodifiers.admin";

    private static string _titlePrefix = "[CSRoll] ";

    /// <summary>
    /// Sets the chat title prefix from config's BannerText, resolving SwiftlyS2's [colorname]
    /// chat color tokens (e.g. "[green]...[default]") via Helper.Colored(). SwiftlyS2 uses square
    /// brackets, not CounterStrikeSharp's curly braces - confirmed via the official porting guide
    /// (swiftlys2.net/docs/guides/porting-from-css) - a "{colorname}" token is left as literal text.
    /// Called once on config load and again on every hot-reload.
    /// </summary>
    public static void SetTitlePrefix(string bannerText)
    {
        _titlePrefix = SwiftlyS2.Shared.Helper.Colored(bannerText);
    }

    /// <summary>Ranged weapon categories - excludes WEAPONTYPE_KNIFE and WEAPONTYPE_C4, so strippers built on this set always leave a player's knife and bomb interactions untouched.</summary>
    public static readonly HashSet<CSWeaponType> AllRangedWeaponTypes =
    [
        CSWeaponType.WEAPONTYPE_PISTOL, CSWeaponType.WEAPONTYPE_SUBMACHINEGUN, CSWeaponType.WEAPONTYPE_RIFLE,
        CSWeaponType.WEAPONTYPE_SHOTGUN, CSWeaponType.WEAPONTYPE_SNIPER_RIFLE, CSWeaponType.WEAPONTYPE_MACHINEGUN,
        CSWeaponType.WEAPONTYPE_TASER, CSWeaponType.WEAPONTYPE_GRENADE,
    ];

    /// <summary>Removes every weapon of the given types from a player, returning the removed item names so the caller can restore them later via RestoreWeapons.</summary>
    public static List<string> StripWeaponTypes(IPlayer player, HashSet<CSWeaponType> typesToStrip)
    {
        var removed = new List<string>();
        if (player.PlayerPawn?.WeaponServices is not { } weaponServices)
        {
            return removed;
        }

        foreach (var weapon in weaponServices.MyValidWeapons.ToList())
        {
            var weaponType = weapon.PlayerWeaponVData?.As<CCSWeaponBaseVData>().WeaponType;
            if (weaponType is not { } type || !typesToStrip.Contains(type))
            {
                continue;
            }

            removed.Add(weapon.DesignerName);
            weaponServices.RemoveWeapon(weapon);
        }

        return removed;
    }

    /// <summary>Gives back a previously-stripped set of item names to a player (counterpart to StripWeaponTypes).</summary>
    public static void RestoreWeapons(IPlayer player, IEnumerable<string> itemNames)
    {
        if (player.PlayerPawn?.ItemServices is not { } itemServices)
        {
            return;
        }

        foreach (var item in itemNames)
        {
            itemServices.GiveItem(item);
        }
    }

    /// <summary>Picks a random dead (non-alive) player on the given team, or null if everyone on that team is alive/absent.</summary>
    public static IPlayer? GetRandomDeadTeammate(ISwiftlyCore core, Team team)
    {
        var deadTeammates = core.PlayerManager.GetInTeam(team).Where(p => p.IsValid && !p.IsAlive).ToList();
        return deadTeammates.Count > 0 ? deadTeammates[Random.Shared.Next(deadTeammates.Count)] : null;
    }

    /// <summary>DMG_BULLET/DMG_BUCKSHOT are the flags CS2 uses for gunfire damage (as opposed to DMG_SLASH for knives or DMG_BLAST for explosives) - confirmed via SwiftlyS2.CS2.dll's DamageTypes_t enum.</summary>
    public static bool IsBulletDamage(DamageTypes_t damageType) =>
        (damageType & (DamageTypes_t.DMG_BULLET | DamageTypes_t.DMG_BUCKSHOT)) != 0;

    public static readonly IReadOnlyList<string> RangedWeaponNames =
    [
        "weapon_deagle", "weapon_elite", "weapon_fiveseven", "weapon_glock", "weapon_hkp2000",
        "weapon_p250", "weapon_tec9", "weapon_usp_silencer", "weapon_cz75a", "weapon_revolver",
        "weapon_mac10", "weapon_mp5sd", "weapon_mp7", "weapon_mp9", "weapon_p90", "weapon_ump45",
        "weapon_bizon", "weapon_ak47", "weapon_aug", "weapon_famas", "weapon_galilar", "weapon_m4a1",
        "weapon_m4a1_silencer", "weapon_sg556", "weapon_ssg08", "weapon_awp", "weapon_g3sg1",
        "weapon_scar20", "weapon_nova", "weapon_xm1014", "weapon_mag7", "weapon_sawedoff",
        "weapon_m249", "weapon_negev",
    ];

    public static string GetRandomRangedWeaponName() => RangedWeaponNames[Random.Shared.Next(RangedWeaponNames.Count)];

    public static void PrintTitleToChat(ISwiftlyCore core, SwiftlyS2.Shared.Players.IPlayer? player, string message)
    {
        player?.SendChat($"{_titlePrefix}{message}");
    }

    public static void PrintTitleToChatAll(ISwiftlyCore core, string message)
    {
        core.PlayerManager.SendChat($"{_titlePrefix}{message}");
    }

    /// <summary>
    /// Like PrintTitleToChat, but also resolves [colorname] tokens in the message body itself -
    /// PrintTitleToChat only ever resolves the cached _titlePrefix (from BannerText), so a per-call
    /// message with its own color tokens (e.g. "[gold]Your modifiers:[default]") needs this instead
    /// or the brackets are sent to chat as literal text.
    /// </summary>
    public static void PrintTitleToChatColored(ISwiftlyCore core, SwiftlyS2.Shared.Players.IPlayer? player, string coloredMessage)
    {
        player?.SendChat($"{_titlePrefix}{SwiftlyS2.Shared.Helper.Colored(coloredMessage)}");
    }

    public static void PrintToChatAll(ISwiftlyCore core, string message)
    {
        core.PlayerManager.SendChat(message);
    }

    /// <summary>
    /// Which specific player got which random modifier is only ever sent here - to connected
    /// admins, not broadcast to the whole server. Nobody else needs to see another player's rolled
    /// modifiers in chat (their own center-HTML banner already tells them privately).
    /// </summary>
    private static IEnumerable<SwiftlyS2.Shared.Players.IPlayer> GetAdmins(ISwiftlyCore core) =>
        core.PlayerManager.GetAllValidPlayers().Where(p => core.Permission.PlayerHasPermission(p.SteamID, AdminPermission));

    public static void PrintTitleToAdminsOnly(ISwiftlyCore core, string message)
    {
        foreach (var admin in GetAdmins(core))
        {
            PrintTitleToChat(core, admin, message);
        }
    }

    public static void PrintToAdminsOnly(ISwiftlyCore core, string message)
    {
        foreach (var admin in GetAdmins(core))
        {
            admin.SendChat(message);
        }
    }

    /// <summary>Sends a styled center-screen HTML message (Panorama rich text, not a real HTML page - see SwiftlyS2's HTML Styling guide for supported tags).</summary>
    public static void ShowMessageCentreAll(ISwiftlyCore core, string html, int durationMs = 5000)
    {
        core.PlayerManager.SendCenterHTML(html, durationMs);
    }

    /// <summary>
    /// Builds the "Activating Modifiers" center banner: a red title the same size as the modifier
    /// entries, then each activated modifier on its own line. Earlier testing with a bare
    /// &lt;br&gt; silently dropped everything after it - CS2's center-print HTML is parsed as
    /// strict XML, and an unclosed &lt;br&gt; tag breaks the parser rather than rendering a line
    /// break. Using the self-closed &lt;br/&gt; form keeps the document well-formed.
    /// </summary>
    public static string BuildActivatingModifiersHtml(ISwiftlyCore core, IReadOnlyCollection<GameModifierBase> modifiers)
    {
        var title = modifiers.Count == 1 ? "Activating Modifier:" : "Activating Modifiers:";
        var lines = new List<string> { $"<span color=\"red\" class=\"fontWeight-bold\">{title}</span>" };
        lines.AddRange(modifiers.Select(m => $"<span color=\"gold\" class=\"fontWeight-bold\">{GetModifierDisplayName(core, m)}</span>"));

        return string.Join("<br/>", lines);
    }

    /// <summary>Builds the green/red "Random Rounds Enabled/Disabled" center banner.</summary>
    public static string BuildRandomRoundsToggleHtml(bool enabled)
    {
        var color = enabled ? "green" : "red";
        var status = enabled ? "Enabled" : "Disabled";
        return $"<span color=\"{color}\" class=\"fontSize-l fontWeight-bold\">Random Rounds {status}</span>";
    }

    /// <summary>
    /// Builds one "still spinning" frame of the slot-machine reveal - deliberately the exact same
    /// red-title/gold-name styling and sizing as BuildActivatingModifiersHtml, so the spin reads as
    /// part of the same reveal rather than a visually distinct effect that then gets swapped out.
    /// </summary>
    public static string BuildSpinFrameHtml(string name)
    {
        return $"<span color=\"red\" class=\"fontWeight-bold\">Rolling...</span><br/><span color=\"gold\" class=\"fontWeight-bold\">{name}</span>";
    }

    /// <summary>
    /// Builds a two-line center-HTML status gauge: a colored label line, then an ASCII progress bar
    /// ("[####----------------] 42%"). Deliberately plain "fontWeight-bold" text with no "fontSize-l"
    /// class - the same size as BuildSpinFrameHtml's "Rolling..." frame - so every gauge popup
    /// (Jetpack fuel, invisibility status, etc.) reads as one consistent HUD family rather than a
    /// mismatched larger element.
    ///
    /// Bug fix: the bar used to mix two different glyphs ('#' for filled, '-' for empty) - Panorama's
    /// UI font is proportional, not monospace, and '#' renders noticeably wider than '-', so the
    /// popup's total line width visibly grew as the bar filled up, eventually wrapping the trailing
    /// "NN%" onto a third line. Fixed by using the SAME glyph ('#') for every cell regardless of
    /// fill state and expressing progress purely through color (barColor vs grey) instead - the
    /// character count and glyph widths are now identical at 0% and 100%, so the line width never
    /// shifts. The percent number is also padded to a constant 3-character field for the same reason.
    /// </summary>
    public static string BuildGaugeHtml(string label, string labelColor, float ratio, string barColor, int barWidth = 20)
    {
        var clamped = Math.Clamp(ratio, 0f, 1f);
        var filled = (int)Math.Round(clamped * barWidth);
        var empty = barWidth - filled;
        var percent = (int)Math.Round(clamped * 100f);

        var filledSegment = filled > 0 ? $"<span color=\"{barColor}\">{new string('#', filled)}</span>" : "";
        var emptySegment = empty > 0 ? $"<span color=\"grey\">{new string('#', empty)}</span>" : "";

        return $"<span color=\"{labelColor}\" class=\"fontWeight-bold\">{label}</span><br/>" +
               $"<span class=\"fontWeight-bold\">[{filledSegment}{emptySegment}] {percent,3}%</span>";
    }

    /// <summary>Gauge-bar color band shared by every percentage-based gauge popup - green when healthy, orange mid, red low.</summary>
    public static string GetGaugeBarColor(float ratio) => ratio switch
    {
        > 0.5f => "lime",
        > 0.2f => "orange",
        _ => "red",
    };

    public static void PrintModifiersToChat(ISwiftlyCore core, SwiftlyS2.Shared.Players.IPlayer? player, IReadOnlyCollection<GameModifierBase> modifiers, string title, bool withDescriptions = true)
    {
        PrintTitleToChat(core, player, title);

        if (modifiers.Count == 0)
        {
            player?.SendChat("None");
            return;
        }

        foreach (var modifier in modifiers)
        {
            var displayName = GetModifierDisplayName(core, modifier);
            var line = withDescriptions ? $"• {displayName} - [{GetModifierDescription(core, modifier)}]" : $"• {displayName}";
            player?.SendChat(line);
        }
    }

    /// <summary>
    /// Looks up a translation key from resources/translations/en.jsonc via SwiftlyS2's own
    /// ILocalizer, returning false (rather than throwing or returning a placeholder) if there's no
    /// entry for it yet. It's unconfirmed whether a missing key returns the key itself, an empty
    /// string, or throws - all three are treated as "not found" here, so callers can fall back to
    /// their own hardcoded default.
    /// </summary>
    private static bool TryLocalize(ISwiftlyCore core, string key, out string value)
    {
        try
        {
            var translated = core.Localizer[key];
            if (!string.IsNullOrEmpty(translated) && translated != key)
            {
                value = translated;
                return true;
            }
        }
        catch
        {
            // No translation key for this yet - fall through to false below.
        }

        value = "";
        return false;
    }

    /// <summary>
    /// Looks up a modifier's chat-shown "explanation" text from resources/translations/en.jsonc
    /// (key: "{Name}.Description"), falling back to the modifier's hardcoded C# Description if no
    /// translation key exists for it yet - so admins can edit that one file to reword any modifier's
    /// description without touching code or rebuilding. Any "{token}" placeholder in the resolved
    /// text (translation OR hardcoded fallback) is then substituted using the modifier's
    /// DynamicTextTokens, if it has any - so a modifier with a live value (a chance rolled
    /// per-activation, a config-driven delay/timer, etc.) can still have its surrounding wording
    /// freely customized in the translation file while always showing the real current value(s).
    /// </summary>
    public static string GetModifierDescription(ISwiftlyCore core, GameModifierBase modifier)
    {
        var description = TryLocalize(core, $"{modifier.Name}.Description", out var value) ? value : modifier.Description;

        if (modifier.DynamicTextTokens is { } tokens)
        {
            foreach (var (token, replacement) in tokens)
            {
                description = description.Replace($"{{{token}}}", replacement);
            }
        }

        return description;
    }

    /// <summary>
    /// Looks up a modifier's chat/HTML-shown display name from resources/translations/en.jsonc (key:
    /// "{Name}.DisplayName"), falling back to the modifier's internal Name if no override exists.
    /// The internal Name itself is never renamed - it's still what IncompatibleModifiers lists,
    /// DisabledModifiers config, and admin commands (!addmodifier etc.) all match against - this only
    /// changes what players actually see printed, e.g. renaming the confusing "DontMiss"/"DropOnMiss"
    /// pair to something clearer without touching any code or breaking existing references to them.
    /// </summary>
    public static string GetModifierDisplayName(ISwiftlyCore core, GameModifierBase modifier) =>
        TryLocalize(core, $"{modifier.Name}.DisplayName", out var value) ? value : modifier.Name;

    public static bool IsWarmupActive(ISwiftlyCore core)
    {
        var gameRules = core.EntitySystem.GetGameRules();
        return gameRules is { } rules && rules.WarmupPeriod;
    }

    // Deliberately no GetModifierName<T>() helper here anymore: it used to instantiate `new T()`
    // to read the Name off another modifier class for IncompatibleModifiers lists. Any pair of
    // modifiers referencing each other that way recurses forever at construction time (A's
    // ctor builds B to read its Name, B's ctor builds A to read its Name, ...) - this crashed
    // the server with a stack overflow the very first time two such modifiers were both
    // registered (Juggernaut <-> GlassCannon). Use plain string literals for cross-references
    // instead; a typo just means an incompatibility silently doesn't match, never a crash.

    /// <summary>
    /// Teleports a player, then temporarily disables their collision for one tick to avoid
    /// a mid-teleport stuck-in-geometry glitch - same safety hack the original CSS plugin used.
    /// </summary>
    public static void TeleportPlayer(ISwiftlyCore core, IPlayer player, Vector position, QAngle? angle = null, Vector? velocity = null)
    {
        if (player.PlayerPawn is not { } pawn)
        {
            return;
        }

        player.Teleport(position, angle ?? pawn.EyeAngles, velocity ?? new Vector(0, 0, 0));

        pawn.Collision.CollisionGroup = (byte)CollisionGroup.Dissolving;
        pawn.Collision.CollisionGroupUpdated();

        var slot = player.Slot;
        core.Scheduler.NextWorldUpdate(() =>
        {
            if (core.PlayerManager.GetPlayer(slot) is { IsValid: true, IsAlive: true } current &&
                current.PlayerPawn is { } currentPawn)
            {
                currentPawn.Collision.CollisionGroup = (byte)CollisionGroup.Player;
                currentPawn.Collision.CollisionGroupUpdated();
            }
        });
    }

    public static void SwapPlayerLocations(ISwiftlyCore core, IPlayer first, IPlayer second)
    {
        if (first.PlayerPawn?.AbsOrigin is not { } firstPosition || second.PlayerPawn?.AbsOrigin is not { } secondPosition)
        {
            return;
        }

        TeleportPlayer(core, first, secondPosition);
        TeleportPlayer(core, second, firstPosition);
    }

    /// <summary>
    /// Resolves a native entity handle (e.g. CTakeDamageInfo.Attacker, which is a raw
    /// CHandle&lt;CEntityInstance&gt; - CTakeDamageInfo has no AttackerPlayer/AttackerPawn
    /// convenience field, unlike the game-event types) to the owning IPlayer, or null if the
    /// handle is empty/invalid or doesn't resolve to a player pawn (e.g. world/NPC damage).
    /// </summary>
    public static IPlayer? GetPlayerFromEntityHandle(ISwiftlyCore core, CHandle<CEntityInstance> handle)
    {
        if (handle.Value is not { } entity)
        {
            return null;
        }

        var player = core.PlayerManager.GetPlayerFromPawn(entity.As<CBasePlayerPawn>());
        return player is { IsValid: true } ? player : null;
    }

    /// <summary>Resolves a thrown grenade projectile's owning player, or null if it's already gone.</summary>
    public static IPlayer? GetThrowerPlayer(ISwiftlyCore core, CBaseCSGrenadeProjectile grenade)
    {
        return grenade.Thrower.Value is { } pawn ? core.PlayerManager.GetPlayerFromPawn(pawn) : null;
    }

    public static Vector? GetSpawnLocation(ISwiftlyCore core, Team team)
    {
        if (team == Team.T)
        {
            var spawns = core.EntitySystem.GetAllEntitiesByDesignerName<CInfoPlayerTerrorist>("info_player_terrorist").ToList();
            return spawns.Count > 0 ? spawns[Random.Shared.Next(spawns.Count)].AbsOrigin : null;
        }

        if (team == Team.CT)
        {
            var spawns = core.EntitySystem.GetAllEntitiesByDesignerName<CInfoPlayerCounterterrorist>("info_player_counterterrorist").ToList();
            return spawns.Count > 0 ? spawns[Random.Shared.Next(spawns.Count)].AbsOrigin : null;
        }

        return null;
    }

    /// <summary>
    /// Picks a random T or CT spawn point, falling back to the other team's spawns if that team
    /// has none. Used by the NavMesh-dependent teleport modifiers when INavMeshService.IsAvailable
    /// is false (signature scan failed) - a guaranteed-reachable, always-available substitute for
    /// GetRandomPosition() so those modifiers still work rather than being hidden from testing.
    /// </summary>
    public static Vector? GetRandomSpawnLocation(ISwiftlyCore core)
    {
        var firstTeam = Random.Shared.Next(2) == 0 ? Team.T : Team.CT;
        var secondTeam = firstTeam == Team.T ? Team.CT : Team.T;

        return GetSpawnLocation(core, firstTeam) ?? GetSpawnLocation(core, secondTeam);
    }
}
