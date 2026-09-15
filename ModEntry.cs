using System.Collections;
using System.Reflection;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Menus;

namespace ConvenientChestsController;

public sealed class ModEntry : Mod
{
    private const string Cc2UniqueId = "SummerFleur.ConvenientChests";
    private const int LockProxyId = 928_410;
    private const int AliasProxyId = 928_411;
    private const int CategoryProxyId = 928_412;

    private Assembly? _cc2Assembly;
    private object? _lastContextRoot;
    private FocusTarget? _focused;
    private bool _wasInManagedContext;

    public override void Entry(IModHelper helper)
    {
        helper.Events.GameLoop.GameLaunched += OnGameLaunched;
        helper.Events.GameLoop.UpdateTicked += OnUpdateTicked;
        helper.Events.Input.ButtonPressed += OnButtonPressed;
        helper.Events.Display.MenuChanged += OnMenuChanged;
    }

    private void OnGameLaunched(object? sender, GameLaunchedEventArgs e)
    {
        if (!Helper.ModRegistry.IsLoaded(Cc2UniqueId))
        {
            Monitor.Log("Convenient Chests 2 isn't loaded; controller patch will stay inactive.", LogLevel.Warn);
            return;
        }

        ResolveCc2Assembly();
        Monitor.Log("Controller patch enabled for Convenient Chests 2.", LogLevel.Info);
    }

    private void OnMenuChanged(object? sender, MenuChangedEventArgs e)
    {
        _focused = null;
        _lastContextRoot = null;
        _wasInManagedContext = false;
    }

    private void OnUpdateTicked(object? sender, UpdateTickedEventArgs e)
    {
        if (!Context.IsWorldReady || Game1.activeClickableMenu is null)
            return;

        if (Game1.activeClickableMenu is ItemGrabMenu itemGrab && TryGetChestOverlay(out object? overlay))
            EnsureRootSideButtonProxies(itemGrab, overlay!);

        object? root = GetManagedContextRoot();
        bool managed = root is not null;
        if (managed && (!ReferenceEquals(root, _lastContextRoot) || !_wasInManagedContext))
        {
            _lastContextRoot = root;
            _wasInManagedContext = true;
            SnapToBestInitialTarget(root!);
        }
        else if (!managed)
        {
            _wasInManagedContext = false;
            _lastContextRoot = null;
            _focused = null;
        }
    }

    private void OnButtonPressed(object? sender, ButtonPressedEventArgs e)
    {
        if (!Context.IsWorldReady || Game1.activeClickableMenu is null)
            return;

        if (TryGetChestOverlay(out object? chestOverlay))
        {
            object? aliasMenu = GetProperty(chestOverlay!, "AliasMenu");
            if (aliasMenu is not null)
            {
                HandleCustomContextInput(aliasMenu, e, isAliasOverlay: true);
                return;
            }

            if (Game1.activeClickableMenu is ItemGrabMenu itemGrab)
            {
                object? lockMenu = GetField(chestOverlay!, "_lockItemMenu");
                bool lockEditMode = lockMenu is not null && GetField(lockMenu, "_editMode") is bool edit && edit;

                if (lockEditMode && IsConfirmButton(e.Button))
                {
                    // Convenient Chests already handles use-tool presses through its overlay.
                    // Convert an action/confirm press into the same click path so either
                    // controller layout can toggle the currently selected inventory slot.
                    if (!e.Button.IsUseToolButton())
                    {
                        Helper.Input.Suppress(e.Button);
                        Point cursor = Game1.getMousePosition();
                        InvokeBool(chestOverlay!, "ReceiveLeftClick", cursor.X, cursor.Y);
                    }
                    return;
                }

                if (IsCancelButton(e.Button))
                {
                    if (InvokeBool(chestOverlay!, "ReceiveKeyPress", Keys.Escape))
                    {
                        Helper.Input.Suppress(e.Button);
                        return;
                    }
                }

                if (IsConfirmButton(e.Button) && TryGetSelectedRootSideButton(itemGrab, chestOverlay!, out Rectangle sideButton))
                {
                    // Use-tool presses are already handled by Convenient Chests. For a normal
                    // action/confirm press, explicitly invoke the overlay's existing click path.
                    if (!e.Button.IsUseToolButton())
                    {
                        Helper.Input.Suppress(e.Button);
                        Point p = sideButton.Center;
                        Game1.setMousePosition(p.X, p.Y);
                        InvokeBool(chestOverlay!, "ReceiveLeftClick", p.X, p.Y);
                    }
                    return;
                }
            }
        }

        if (IsCc2CustomActiveMenu(Game1.activeClickableMenu))
            HandleCustomContextInput(GetManagedContextRoot() ?? Game1.activeClickableMenu, e, isAliasOverlay: false);
    }

    private bool TryGetSelectedRootSideButton(ItemGrabMenu menu, object chestOverlay, out Rectangle bounds)
    {
        bounds = default;
        int selectedId = menu.currentlySnappedComponent?.myID ?? -1;
        string? key = selectedId switch
        {
            LockProxyId => "Lock",
            AliasProxyId => "Alias",
            CategoryProxyId => "Category",
            _ => null
        };

        if (key is null)
            return false;

        var buttonBounds = GetRootSideButtonBounds(chestOverlay);
        return buttonBounds.TryGetValue(key, out bounds);
    }

    private void HandleCustomContextInput(object root, ButtonPressedEventArgs e, bool isAliasOverlay)
    {
        Direction? direction = ToDirection(e.Button);
        if (direction is not null)
        {
            Helper.Input.Suppress(e.Button);

            if (isAliasOverlay && IsAliasTextBoxSelected(root))
                InvokeBool(root, "ReceiveKeyPress", Keys.Escape);

            MoveFocus(root, direction.Value);
            return;
        }

        if (IsCancelButton(e.Button))
        {
            Helper.Input.Suppress(e.Button);
            if (isAliasOverlay)
                InvokeBool(root, "ReceiveKeyPress", Keys.Escape);
            else
                Game1.activeClickableMenu.receiveKeyPress(Keys.Escape);
            _focused = null;
            return;
        }

        if (IsConfirmButton(e.Button))
        {
            // The overlay already handles use-tool presses. Only translate the normal
            // action/confirm button there to avoid triggering the same control twice.
            if (isAliasOverlay && e.Button.IsUseToolButton())
                return;

            if (_focused is null)
                SnapToBestInitialTarget(root);

            if (_focused is not null)
            {
                Helper.Input.Suppress(e.Button);
                Point p = _focused.Value.Bounds.Center;
                Game1.setMousePosition(p.X, p.Y);

                if (isAliasOverlay)
                    InvokeBool(root, "ReceiveLeftClick", p.X, p.Y);
                else
                    Game1.activeClickableMenu.receiveLeftClick(p.X, p.Y, true);

                _focused = null; // menu state may have changed (dropdown/submenu/etc.)
                object? newRoot = GetManagedContextRoot();
                if (newRoot is not null)
                    SnapToNearestTarget(newRoot, p);
            }
            return;
        }

        // Shoulder buttons page through a focused CC2 grid/dropdown when possible.
        if (e.Button is SButton.LeftShoulder or SButton.RightShoulder)
        {
            if (_focused?.ScrollHost is object scrollHost)
            {
                Helper.Input.Suppress(e.Button);
                int amount = e.Button == SButton.LeftShoulder ? 1 : -1;
                if (InvokeBool(scrollHost, "ReceiveScrollWheelAction", amount))
                {
                    Point old = _focused.Value.Bounds.Center;
                    _focused = null;
                    SnapToNearestTarget(root, old);
                }
            }
        }
    }

    private void EnsureRootSideButtonProxies(ItemGrabMenu menu, object chestOverlay)
    {
        if (!Game1.options.SnappyMenus || menu.allClickableComponents is null || menu.fillStacksButton is null)
            return;

        if (menu.allClickableComponents.Any(p => p.myID == AliasProxyId))
            return;

        var bounds = GetRootSideButtonBounds(chestOverlay);
        if (bounds.Count == 0)
            return;

        int vanillaId = menu.fillStacksButton.myID;
        if (vanillaId < 0)
        {
            menu.populateClickableComponentList();
            vanillaId = menu.fillStacksButton.myID;
        }

        ClickableComponent? lockProxy = CreateProxy(bounds.GetValueOrDefault("Lock"), "CC2 Lock Items", LockProxyId);
        ClickableComponent? aliasProxy = CreateProxy(bounds.GetValueOrDefault("Alias"), "CC2 Alias", AliasProxyId);
        ClickableComponent? categoryProxy = CreateProxy(bounds.GetValueOrDefault("Category"), "CC2 Categorize", CategoryProxyId);

        if (aliasProxy is null)
            return;

        aliasProxy.leftNeighborID = vanillaId;
        aliasProxy.upNeighborID = lockProxy?.myID ?? -1;
        aliasProxy.downNeighborID = categoryProxy?.myID ?? -1;

        if (lockProxy is not null)
        {
            lockProxy.leftNeighborID = vanillaId;
            lockProxy.downNeighborID = aliasProxy.myID;
            menu.allClickableComponents.Add(lockProxy);
        }

        menu.allClickableComponents.Add(aliasProxy);

        if (categoryProxy is not null)
        {
            categoryProxy.leftNeighborID = vanillaId;
            categoryProxy.upNeighborID = aliasProxy.myID;
            menu.allClickableComponents.Add(categoryProxy);
        }

        menu.fillStacksButton.rightNeighborID = AliasProxyId;
    }

    private static ClickableComponent? CreateProxy(Rectangle bounds, string name, int id)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0)
            return null;

        return new ClickableComponent(bounds, name)
        {
            myID = id,
            upNeighborID = -1,
            downNeighborID = -1,
            leftNeighborID = -1,
            rightNeighborID = -1,
            fullyImmutable = true
        };
    }

    private Dictionary<string, Rectangle> GetRootSideButtonBounds(object chestOverlay)
    {
        var result = new Dictionary<string, Rectangle>();

        object? lockMenu = GetField(chestOverlay, "_lockItemMenu");
        object? lockButton = lockMenu is null ? null : GetProperty(lockMenu, "LockButton", includeNonPublic: true);
        object? aliasButton = GetProperty(chestOverlay, "AliasButton", includeNonPublic: true);
        object? categoryButton = GetProperty(chestOverlay, "CategorizeButton", includeNonPublic: true);

        if (TryGetBounds(lockButton, out Rectangle lockBounds)) result["Lock"] = lockBounds;
        if (TryGetBounds(aliasButton, out Rectangle aliasBounds)) result["Alias"] = aliasBounds;
        if (TryGetBounds(categoryButton, out Rectangle categoryBounds)) result["Category"] = categoryBounds;

        return result;
    }

    private object? GetManagedContextRoot()
    {
        if (TryGetChestOverlay(out object? overlay))
        {
            object? alias = GetProperty(overlay!, "AliasMenu");
            if (alias is not null)
            {
                if (GetField(alias, "_itemPickerOn") is bool pickerOn && pickerOn)
                    return GetField(alias, "_itemPicker") ?? alias;
                return alias;
            }
        }

        IClickableMenu? menu = Game1.activeClickableMenu;
        if (menu is null || !IsCc2CustomActiveMenu(menu))
            return null;

        object? submenu = GetProperty(menu, "SubMenu", includeNonPublic: true)
                          ?? GetField(menu, "SubMenu")
                          ?? GetField(menu, "_subMenu");
        return submenu ?? menu;
    }

    private void SnapToBestInitialTarget(object root)
    {
        List<FocusTarget> targets = CollectFocusTargets(root);
        if (targets.Count == 0)
            return;

        Point cursor = Game1.getMousePosition();
        _focused = targets.OrderBy(t => DistanceSquared(t.Bounds.Center, cursor)).First();
        MoveCursor(_focused.Value.Bounds.Center);
    }

    private void SnapToNearestTarget(object root, Point around)
    {
        List<FocusTarget> targets = CollectFocusTargets(root);
        if (targets.Count == 0)
        {
            _focused = null;
            return;
        }

        _focused = targets.OrderBy(t => DistanceSquared(t.Bounds.Center, around)).First();
        MoveCursor(_focused.Value.Bounds.Center);
    }

    private void MoveFocus(object root, Direction direction)
    {
        List<FocusTarget> targets = CollectFocusTargets(root);
        if (targets.Count == 0)
            return;

        if (_focused is null || !targets.Any(t => SameTarget(t, _focused.Value)))
        {
            Point cursor = Game1.getMousePosition();
            _focused = targets.OrderBy(t => DistanceSquared(t.Bounds.Center, cursor)).First();
            MoveCursor(_focused.Value.Bounds.Center);
            return;
        }

        FocusTarget current = _focused.Value;
        FocusTarget? next = FindDirectionalTarget(current, targets, direction);

        if (next is null && current.ScrollHost is not null && direction is Direction.Up or Direction.Down)
        {
            int amount = direction == Direction.Up ? 1 : -1;
            if (InvokeBool(current.ScrollHost, "ReceiveScrollWheelAction", amount))
            {
                Point old = current.Bounds.Center;
                targets = CollectFocusTargets(root);
                next = FindDirectionalTargetFromPoint(old, targets, direction)
                       ?? targets.OrderBy(t => DistanceSquared(t.Bounds.Center, old)).FirstOrDefault();
            }
        }

        if (next is null)
            return;

        _focused = next;
        MoveCursor(next.Value.Bounds.Center);
    }

    private static FocusTarget? FindDirectionalTarget(FocusTarget current, List<FocusTarget> targets, Direction direction)
        => FindDirectionalTargetFromPoint(current.Bounds.Center, targets.Where(t => !SameTarget(t, current)).ToList(), direction);

    private static FocusTarget? FindDirectionalTargetFromPoint(Point origin, List<FocusTarget> targets, Direction direction)
    {
        FocusTarget? best = null;
        double bestScore = double.MaxValue;

        foreach (FocusTarget candidate in targets)
        {
            Point c = candidate.Bounds.Center;
            int dx = c.X - origin.X;
            int dy = c.Y - origin.Y;

            int primary;
            int perpendicular;
            bool valid;
            switch (direction)
            {
                case Direction.Up:
                    valid = dy < -4;
                    primary = -dy;
                    perpendicular = Math.Abs(dx);
                    break;
                case Direction.Down:
                    valid = dy > 4;
                    primary = dy;
                    perpendicular = Math.Abs(dx);
                    break;
                case Direction.Left:
                    valid = dx < -4;
                    primary = -dx;
                    perpendicular = Math.Abs(dy);
                    break;
                default:
                    valid = dx > 4;
                    primary = dx;
                    perpendicular = Math.Abs(dy);
                    break;
            }

            if (!valid)
                continue;

            // Strongly prefer controls in the same row/column, but allow diagonals when needed.
            double score = primary + perpendicular * 2.75;
            if (score < bestScore)
            {
                bestScore = score;
                best = candidate;
            }
        }

        return best;
    }

    private List<FocusTarget> CollectFocusTargets(object root)
    {
        var result = new List<FocusTarget>();
        var seenObjects = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var seenRects = new HashSet<(int X, int Y, int W, int H, string Type)>();
        WalkUiObject(root, result, seenObjects, seenRects, scrollHost: null, depth: 0);

        Rectangle viewport = new(0, 0, Game1.uiViewport.Width, Game1.uiViewport.Height);
        return result
            .Where(t => t.Bounds.Width > 4 && t.Bounds.Height > 4 && viewport.Intersects(t.Bounds))
            .OrderBy(t => t.Bounds.Y)
            .ThenBy(t => t.Bounds.X)
            .ToList();
    }

    private void WalkUiObject(
        object? obj,
        List<FocusTarget> result,
        HashSet<object> seenObjects,
        HashSet<(int X, int Y, int W, int H, string Type)> seenRects,
        object? scrollHost,
        int depth)
    {
        if (obj is null || depth > 9)
            return;

        Type type = obj.GetType();
        if (IsSimple(type) || type == typeof(string))
            return;

        if (!type.IsValueType && !seenObjects.Add(obj))
            return;

        string fullName = type.FullName ?? type.Name;
        bool isUiType = fullName.StartsWith("UI.", StringComparison.Ordinal)
                        || fullName.StartsWith("ConvenientChests.", StringComparison.Ordinal);

        if (fullName.StartsWith("UI.Menu.GridMenu", StringComparison.Ordinal))
            scrollHost = obj;

        if (fullName.StartsWith("UI.Menu.DropDownMenu", StringComparison.Ordinal))
        {
            AddDropDownTargets(obj, result, seenRects, scrollHost);
            // Don't add the drop-down's decorative internals.
            return;
        }

        if (isUiType && ImplementsInterface(type, "UI.Component.IClickableComponent"))
        {
            bool isContainer = fullName.StartsWith("UI.Menu.", StringComparison.Ordinal);
            if (!isContainer && TryGetBounds(obj, out Rectangle bounds))
                AddTarget(result, seenRects, new FocusTarget(bounds, obj, scrollHost, fullName));
        }

        if (obj is IEnumerable enumerable && obj is not string)
        {
            int count = 0;
            foreach (object? item in enumerable)
            {
                if (++count > 500) break;
                WalkUiObject(item, result, seenObjects, seenRects, scrollHost, depth + 1);
            }
            return;
        }

        if (!isUiType)
            return;

        BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        foreach (FieldInfo field in type.GetFields(flags))
        {
            if (field.IsStatic || ShouldSkipMemberType(field.FieldType))
                continue;
            try
            {
                object? value = field.GetValue(obj);
                WalkUiObject(value, result, seenObjects, seenRects, scrollHost, depth + 1);
            }
            catch { /* third-party UI reflection: ignore inaccessible/problematic members */ }
        }

        foreach (PropertyInfo prop in type.GetProperties(flags))
        {
            if (!prop.CanRead || prop.GetIndexParameters().Length != 0 || ShouldSkipMemberType(prop.PropertyType))
                continue;
            if (prop.Name is "Parent" or "RootMenu" or "Chest" or "Item" or "Tooltip" or "Background" or "Texture")
                continue;
            try
            {
                object? value = prop.GetValue(obj);
                WalkUiObject(value, result, seenObjects, seenRects, scrollHost, depth + 1);
            }
            catch { }
        }
    }

    private void AddDropDownTargets(
        object dropdown,
        List<FocusTarget> result,
        HashSet<(int X, int Y, int W, int H, string Type)> seenRects,
        object? scrollHost)
    {
        if (!TryGetBounds(dropdown, out Rectangle bounds))
            return;

        bool expanded = GetField(dropdown, "Expanded") as bool? ?? GetProperty(dropdown, "Expanded") as bool? ?? false;
        string typeName = dropdown.GetType().FullName ?? "DropDown";

        int baseHeight = GetField(dropdown, "_height") as int? ?? 60;
        Rectangle header = new(bounds.X, bounds.Y, bounds.Width, baseHeight);
        AddTarget(result, seenRects, new FocusTarget(header, dropdown, scrollHost ?? dropdown, typeName + ":Header"));

        if (!expanded)
            return;

        int first = GetField(dropdown, "_firstVisibleIndex") as int? ?? 0;
        int maxVisible = GetProperty(dropdown, "MaxVisibleOptions") as int? ?? 0;
        object? optionsObj = GetField(dropdown, "Options") ?? GetProperty(dropdown, "Options");
        int optionCount = optionsObj is ICollection collection ? collection.Count : maxVisible;
        int visible = Math.Min(maxVisible, Math.Max(0, optionCount - first));

        for (int i = 0; i < visible; i++)
        {
            Rectangle optionRect = new(bounds.X, bounds.Y + baseHeight + i * 40, bounds.Width, 40);
            AddTarget(result, seenRects, new FocusTarget(optionRect, dropdown, dropdown, typeName + $":Option{first + i}"));
        }
    }

    private static void AddTarget(
        List<FocusTarget> result,
        HashSet<(int X, int Y, int W, int H, string Type)> seen,
        FocusTarget target)
    {
        var key = (target.Bounds.X, target.Bounds.Y, target.Bounds.Width, target.Bounds.Height, target.DebugType);
        if (seen.Add(key))
            result.Add(target);
    }

    private static bool SameTarget(FocusTarget a, FocusTarget b)
        => a.Bounds == b.Bounds && a.DebugType == b.DebugType;

    private static bool IsAliasTextBoxSelected(object aliasMenu)
    {
        object? textBox = GetField(aliasMenu, "_textBox");
        object? selected = textBox is null ? null : GetProperty(textBox, "Selected", includeNonPublic: true);
        return selected is bool b && b;
    }

    private bool TryGetChestOverlay(out object? overlay)
    {
        overlay = null;
        ResolveCc2Assembly();
        if (_cc2Assembly is null)
            return false;

        Type? manager = _cc2Assembly.GetType("ConvenientChests.Framework.UserInterfaceService.MenuManager");
        if (manager is null)
            return false;

        object? perScreen = manager.GetProperty("ScreenWidgetHost", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(null);
        object? host = perScreen is null ? null : GetProperty(perScreen, "Value", includeNonPublic: true);
        overlay = host is null ? null : GetField(host, "Overlay");
        return overlay is not null && overlay.GetType().FullName == "ConvenientChests.Framework.UserInterfaceService.ChestOverlay";
    }

    private void ResolveCc2Assembly()
    {
        if (_cc2Assembly is not null)
            return;

        _cc2Assembly = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(a => a.GetName().Name?.Equals("ConvenientChests", StringComparison.OrdinalIgnoreCase) == true)
            ?? AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetType("ConvenientChests.Framework.UserInterfaceService.MenuManager", false) is not null);
    }

    private bool IsCc2CustomActiveMenu(IClickableMenu menu)
    {
        ResolveCc2Assembly();
        Type type = menu.GetType();
        return _cc2Assembly is not null
               && ReferenceEquals(type.Assembly, _cc2Assembly)
               && type.Namespace?.StartsWith("ConvenientChests.", StringComparison.Ordinal) == true
               && menu is not ItemGrabMenu;
    }

    private static void MoveCursor(Point point)
    {
        int x = Math.Clamp(point.X, 0, Math.Max(0, Game1.uiViewport.Width - 1));
        int y = Math.Clamp(point.Y, 0, Math.Max(0, Game1.uiViewport.Height - 1));
        Game1.setMousePosition(x, y);
    }

    private static Direction? ToDirection(SButton button) => button switch
    {
        SButton.DPadUp or SButton.LeftThumbstickUp => Direction.Up,
        SButton.DPadDown or SButton.LeftThumbstickDown => Direction.Down,
        SButton.DPadLeft or SButton.LeftThumbstickLeft => Direction.Left,
        SButton.DPadRight or SButton.LeftThumbstickRight => Direction.Right,
        _ => null
    };

    private static bool IsConfirmButton(SButton button)
        => button.IsActionButton() || button.IsUseToolButton();

    private static bool IsCancelButton(SButton button)
        => button == SButton.ControllerB;

    private static bool TryGetBounds(object? obj, out Rectangle bounds)
    {
        bounds = default;
        if (obj is null)
            return false;

        object? value = GetProperty(obj, "Bounds", includeNonPublic: true);
        if (value is Rectangle rect)
        {
            bounds = rect;
            return true;
        }

        return false;
    }

    private static object? GetField(object obj, string name)
    {
        for (Type? t = obj.GetType(); t is not null; t = t.BaseType)
        {
            FieldInfo? field = t.GetField(name, BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (field is not null)
                return field.GetValue(obj);
        }
        return null;
    }

    private static object? GetProperty(object obj, string name, bool includeNonPublic = false)
    {
        BindingFlags flags = BindingFlags.Instance | BindingFlags.Public;
        if (includeNonPublic) flags |= BindingFlags.NonPublic;
        for (Type? t = obj.GetType(); t is not null; t = t.BaseType)
        {
            PropertyInfo? prop = t.GetProperty(name, flags);
            if (prop is not null && prop.GetIndexParameters().Length == 0)
            {
                try { return prop.GetValue(obj); }
                catch { return null; }
            }
        }
        return null;
    }

    private static bool InvokeBool(object obj, string methodName, params object[] args)
    {
        BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        MethodInfo? method = obj.GetType().GetMethods(flags)
            .FirstOrDefault(m => m.Name == methodName && m.GetParameters().Length == args.Length);
        if (method is null)
            return false;

        try
        {
            object? result = method.Invoke(obj, args);
            return result is bool b && b;
        }
        catch
        {
            return false;
        }
    }

    private static bool ImplementsInterface(Type type, string fullName)
        => type.GetInterfaces().Any(i => i.FullName == fullName);

    private static bool IsSimple(Type type)
        => type.IsPrimitive || type.IsEnum || type == typeof(decimal) || type == typeof(DateTime) || type == typeof(Guid);

    private static bool ShouldSkipMemberType(Type type)
    {
        string? ns = type.Namespace;
        if (type == typeof(string) || type.IsPrimitive || type.IsEnum)
            return true;
        if (ns?.StartsWith("Microsoft.Xna.Framework.Graphics", StringComparison.Ordinal) == true)
            return true;
        if (ns?.StartsWith("StardewValley.Objects", StringComparison.Ordinal) == true)
            return true;
        return false;
    }

    private static long DistanceSquared(Point a, Point b)
    {
        long dx = a.X - b.X;
        long dy = a.Y - b.Y;
        return dx * dx + dy * dy;
    }

    private enum Direction { Up, Down, Left, Right }

    private readonly record struct FocusTarget(Rectangle Bounds, object Owner, object? ScrollHost, string DebugType);

    private sealed class ReferenceEqualityComparer : IEqualityComparer<object>
    {
        public static ReferenceEqualityComparer Instance { get; } = new();
        public new bool Equals(object? x, object? y) => ReferenceEquals(x, y);
        public int GetHashCode(object obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
    }
}
