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

    /// <summary>
    /// Shared chrome palette for every CSRoll menu - applied once per menu via its Design API so
    /// nothing is left at the framework's plain white/grey default. Colors are set through
    /// IMenuBuilderAPI.Design (reached via menu.Builder.Design) rather than embedded in option TEXT,
    /// deliberately: these apply to menu-level chrome (footer/nav marker/guide line/disabled state)
    /// with no effect on any option's text length, so they can't make the text-truncation issue worse.
    /// </summary>
    private static void ApplyMenuTheme(IMenuAPI menu)
    {
        if (menu.Builder?.Design is not { } design)
        {
            return;
        }

        design.SetMenuFooterColor("#FFB347");
        design.SetNavigationMarkerColor("#FFD700");
        design.SetVisualGuideLineColor("#FF8C00");
        design.SetDisabledColor("#888888");
    }

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

    /// <summary>
    /// Every CreateMenu call in this file passes ScrollLeftFade explicitly instead of taking the
    /// framework's default (TruncateEnd). Reported bug: option/comment text longer than the panel's
    /// display width was being permanently cut off with the default style rather than made readable -
    /// ScrollLeftFade instead scrolls the full text through view over a couple of seconds, so nothing
    /// is ever permanently hidden. Labels are also kept short throughout this file so short text
    /// simply displays normally and only genuinely long text (a modifier's full display name, say)
    /// ever needs to scroll at all.
    /// </summary>
    private IMenuAPI CreateThemedMenu(MenuConfiguration configuration)
    {
        var menu = Core.MenusAPI.CreateMenu(configuration, new MenuKeybindOverrides(), optionTextStyle: MenuOptionTextStyle.ScrollLeftFade);
        ApplyMenuTheme(menu);
        return menu;
    }

    private IMenuAPI BuildRootMenu()
    {
        var menu = CreateThemedMenu(new MenuConfiguration { Title = "CSRoll Configuration" });

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

        menu.AddOption(new SubmenuMenuOption("Modifiers Per Player", BuildCountMenu));
        menu.AddOption(new SubmenuMenuOption("Enable/Disable Rolling", BuildModifierToggleMenu));

        var reroll = new ButtonMenuOption("Re-roll Modifiers") { CloseAfterClick = true };
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

        var clear = new ButtonMenuOption("Remove All Active") { CloseAfterClick = true };
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
        var menu = CreateThemedMenu(new MenuConfiguration
        {
            Title = "Modifiers Per Player",
            DefaultComment = "Runtime only (resets on map change)",
        });

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
    /// One toggle per modifier CSRoll knows about (Runtime.GetAllKnownModifiers() - not just the
    /// currently-registered ones), controlling whether it's REGISTERED and therefore eligible to be
    /// rolled/added at all - the same thing !disablemodifier/EnableModifierByName do, not whether it's
    /// currently ACTIVE. An earlier version of this menu wired the toggle to live active state (the
    /// same thing !rolltoggle does); that was wrong - this list exists to curate the rollable pool
    /// (e.g. "never roll Jetpack on this server"), not to hand-activate/deactivate individual
    /// modifiers, which !rolltoggle/!memodifier already do.
    ///
    /// Toggling off a modifier that's currently active deactivates it too (DisableModifierByName's
    /// existing behavior - a disabled modifier has no business staying active for whoever already
    /// rolled it). Toggling on a previously-disabled modifier only makes it eligible again; it does
    /// not itself activate anything.
    /// </summary>
    private IMenuAPI BuildModifierToggleMenu()
    {
        var menu = CreateThemedMenu(new MenuConfiguration
        {
            Title = "Enable/Disable Rolling",
            DefaultComment = "On = can be rolled/added",
        });

        var known = Runtime.GetAllKnownModifiers().OrderBy(m => CSRollUtils.GetModifierDisplayName(Core, m), StringComparer.OrdinalIgnoreCase).ToList();

        foreach (var modifier in known)
        {
            // Captured per-iteration so each closure refers to its own modifier, not the loop's last.
            var name = modifier.Name;
            var label = $"<span color=\"gold\">{CSRollUtils.GetModifierDisplayName(Core, modifier)}</span>";
            var option = new ToggleMenuOption(label, Runtime.IsModifierRegisteredByName(name));

            option.Click += (_, args) =>
            {
                var wantsEnabled = option.GetToggleState(args.Player);
                var ok = wantsEnabled
                    ? Runtime.EnableModifierByName(name, out var message)
                    : Runtime.DisableModifierByName(name, out message);

                if (!ok)
                {
                    // Snap the toggle back to reality and explain why (e.g. asked to enable something
                    // already enabled from a stale menu view after another admin changed it first).
                    option.SetToggleState(args.Player, Runtime.IsModifierRegisteredByName(name));
                    CSRollUtils.PrintTitleToChat(Core, args.Player, message);
                }

                return ValueTask.CompletedTask;
            };

            menu.AddOption(option);
        }

        if (known.Count == 0)
        {
            menu.AddOption(new TextMenuOption("No modifiers registered"));
        }

        return menu;
    }
}
