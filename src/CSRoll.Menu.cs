using Microsoft.Extensions.Logging;

using SwiftlyS2.Core.Menus.OptionsBase;
using SwiftlyS2.Shared.Commands;
using SwiftlyS2.Shared.Menus;
using SwiftlyS2.Shared.Players;

using CSRoll.Core;
using CSRoll.Modifiers;

namespace CSRoll;

/// <summary>
/// The !rollmenu admin configuration menu.
///
/// Deliberately built on SwiftlyS2's menu system rather than the plugin's own center-HTML popups,
/// because this is the one place in CSRoll where menus are actually the right tool: they capture
/// player input (movement keys become menu navigation), which is exactly wrong for a transient
/// reveal banner but exactly right for an interactive settings screen. Note this is also why the
/// menu is NOT used for the spin/reveal - it renders into the same underlying panel via the same
/// game event and offers no extra styling capability, only input handling.
///
/// Every menu here is rebuilt fresh each time it's opened (via the Func&lt;IMenuAPI&gt; submenu
/// overload) rather than constructed once and reused: the modifier list, the active/inactive state
/// of each modifier, and the random-round settings can all change between openings - from another
/// admin's chat command, a config hot-reload, or the automatic per-round roll - and a cached menu
/// would keep showing whatever was true when it was first built.
/// </summary>
public partial class CSRoll
{
    /// <summary>
    /// Upper bound offered by the Min/Max sliders. Not a hard engine limit - just the point past
    /// which "give every player N simultaneous modifiers" stops being playable, and a slider that
    /// ran to 50 would be tedious to drag through. Config.jsonc can still be edited by hand to go
    /// higher; this only bounds what the menu itself offers.
    /// </summary>
    private const int MenuMaxRandomRounds = 10;

    private void InitializeMenu()
    {
        _commandGuids.Add(Core.Command.RegisterCommand("rollmenu", Debounce("rollmenu", OnRollMenu), registerRaw: true, permission: AdminPermission, helpText: "Opens the CSRoll configuration menu."));
    }

    public void OnRollMenu(ICommandContext context)
    {
        if (context.Sender is not { IsValid: true } sender)
        {
            CSRollUtils.PrintTitleToChat(Core, context.Sender, "Only an in-game player can use this command.");
            return;
        }

        Core.MenusAPI.OpenMenuForPlayer(sender, BuildRootMenu());
    }

    private IMenuAPI BuildRootMenu()
    {
        var menu = Core.MenusAPI.CreateMenu(new MenuConfiguration { Title = "CSRoll Configuration" }, new MenuKeybindOverrides());

        // Random rounds on/off. Reads the CURRENT state as the toggle's default so the menu always
        // opens reflecting reality rather than a stale value, and routes the change through
        // Runtime.ToggleRandomRounds() (not a direct property write) so the existing broadcast
        // chat/banner announcement still happens exactly as it does for !randomrounds.
        var randomRounds = new ToggleMenuOption("Random Rounds", Runtime.RandomRoundsEnabled);
        randomRounds.Click += (_, args) =>
        {
            // The toggle flips its own visual state on click; ToggleRandomRounds() flips the real
            // one. Guard against them drifting apart if a second admin toggles it concurrently.
            if (randomRounds.GetToggleState(args.Player) != Runtime.RandomRoundsEnabled)
            {
                Runtime.ToggleRandomRounds();
            }

            return ValueTask.CompletedTask;
        };
        menu.AddOption(randomRounds);

        menu.AddOption(new SubmenuMenuOption("Modifiers per player", BuildCountMenu));
        menu.AddOption(new SubmenuMenuOption("Enable / disable modifiers", BuildModifierToggleMenu));

        var reroll = new ButtonMenuOption("Re-roll this round's modifiers") { CloseAfterClick = true };
        reroll.Click += (_, args) =>
        {
            if (!Runtime.RandomRoundsEnabled || Runtime.RegisteredModifiers.Count == 0)
            {
                CSRollUtils.PrintTitleToChat(Core, args.Player, "Random rounds are not enabled, or no modifiers are registered.");
                return ValueTask.CompletedTask;
            }

            Runtime.RemoveAllModifiers();
            Runtime.ApplyRandomRoundsForRound();
            return ValueTask.CompletedTask;
        };
        menu.AddOption(reroll);

        var clear = new ButtonMenuOption("Remove all active modifiers") { CloseAfterClick = true };
        clear.Click += (_, args) =>
        {
            Runtime.RemoveAllModifiers();
            CSRollUtils.PrintTitleToChat(Core, args.Player, "Removed all modifiers.");
            return ValueTask.CompletedTask;
        };
        menu.AddOption(clear);

        return menu;
    }

    /// <summary>
    /// Min/Max modifiers per player. These are the same values !minrandomrounds/!maxrandomrounds used
    /// to set before they became config-only - the menu reintroduces runtime control over them, but
    /// deliberately does NOT write back to config.jsonc, so a change here lasts until the next full
    /// plugin reload (map change/restart) and then reverts to whatever the file says. That's stated
    /// in the menu's own comment line so an admin isn't surprised by it.
    ///
    /// The two sliders clamp against each other on change: Min can never be dragged above Max and
    /// vice versa, because ModifierRuntime.RollRandomRoundCount would otherwise be handed an inverted
    /// range (its own internal clamp already degrades that safely, but silently rolling "exactly Min"
    /// when the admin thinks they set a range is worse than just not letting the pair go invalid).
    /// </summary>
    private IMenuAPI BuildCountMenu()
    {
        var menu = Core.MenusAPI.CreateMenu(new MenuConfiguration
        {
            Title = "Modifiers per player",
            DefaultComment = "Runtime only - reverts to config.jsonc on map change",
        }, new MenuKeybindOverrides());

        var min = new SliderMenuOption("Minimum", 1f, MenuMaxRandomRounds, Runtime.MinRandomRounds, 1f);
        var max = new SliderMenuOption("Maximum", 1f, MenuMaxRandomRounds, Runtime.MaxRandomRounds, 1f);

        min.Click += (_, args) =>
        {
            var value = (int)min.GetValue(args.Player);
            if (value > Runtime.MaxRandomRounds)
            {
                value = Runtime.MaxRandomRounds;
                min.SetValue(args.Player, value);
            }

            Runtime.MinRandomRounds = value;
            return ValueTask.CompletedTask;
        };

        max.Click += (_, args) =>
        {
            var value = (int)max.GetValue(args.Player);
            if (value < Runtime.MinRandomRounds)
            {
                value = Runtime.MinRandomRounds;
                max.SetValue(args.Player, value);
            }

            Runtime.MaxRandomRounds = value;
            return ValueTask.CompletedTask;
        };

        menu.AddOption(min);
        menu.AddOption(max);
        return menu;
    }

    /// <summary>
    /// One toggle per registered modifier, reflecting and controlling whether it's currently ACTIVE
    /// (not whether it's registered) - i.e. the same thing !rolltoggle does, just browsable.
    ///
    /// Toggling a modifier on from here activates it GLOBALLY (empty AssignedSlots = everyone), which
    /// is what !rolltoggle does too. Per-player scoping stays with !memodifier and the automatic
    /// per-round roll; expressing "activate for just player X" would need a player-picker submenu per
    /// modifier, which is a much bigger UI and not what was asked for.
    ///
    /// Modifiers already active for SPECIFIC players (from a per-player roll) show as on, and
    /// toggling them off removes them from everyone currently assigned - again matching !rolltoggle's
    /// documented whole-instance behaviour rather than inventing a different rule here.
    /// </summary>
    private IMenuAPI BuildModifierToggleMenu()
    {
        var menu = Core.MenusAPI.CreateMenu(new MenuConfiguration
        {
            Title = "Enable / disable modifiers",
            DefaultComment = "Toggling on activates globally (everyone)",
        }, new MenuKeybindOverrides());

        foreach (var modifier in Runtime.RegisteredModifiers.OrderBy(m => CSRollUtils.GetModifierDisplayName(Core, m), StringComparer.OrdinalIgnoreCase))
        {
            // Captured per-iteration so each closure refers to its own modifier, not the loop's last.
            var captured = modifier;
            var option = new ToggleMenuOption(CSRollUtils.GetModifierDisplayName(Core, captured), Runtime.IsModifierActive(captured));

            option.Click += (_, args) =>
            {
                if (!Runtime.ToggleModifier(captured, out var message))
                {
                    // Blocked (e.g. incompatible with something already active) - put the toggle back
                    // to the real state so the menu doesn't show an "on" switch for something that
                    // never actually activated, and tell the admin why.
                    option.SetToggleState(args.Player, Runtime.IsModifierActive(captured));
                    CSRollUtils.PrintTitleToChat(Core, args.Player, message);
                }

                return ValueTask.CompletedTask;
            };

            menu.AddOption(option);
        }

        if (Runtime.RegisteredModifiers.Count == 0)
        {
            menu.AddOption(new TextMenuOption("No modifiers registered"));
        }

        return menu;
    }
}
