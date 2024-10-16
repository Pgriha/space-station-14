using Content.Shared.ActionBlocker;
using Content.Shared.Administration.Managers;
using Content.Shared.Ghost;
using Content.Shared.Hands;
using Content.Shared.Hands.Components;
using Content.Shared.Interaction;
using Content.Shared.Interaction.Events;
using Content.Shared.Popups;
using Content.Shared.Verbs;
using Robust.Shared.Player;

namespace Content.Shared.UserInterface;

public sealed partial class ActivatableUISystem : EntitySystem
{
    [Dependency] private readonly ISharedAdminManager _adminManager = default!;
    [Dependency] private readonly ActionBlockerSystem _blockerSystem = default!;
    [Dependency] private readonly SharedUserInterfaceSystem _uiSystem = default!;
    [Dependency] private readonly SharedPopupSystem _popupSystem = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<ActivatableUIComponent, ActivateInWorldEvent>(OnActivate);
        SubscribeLocalEvent<ActivatableUIComponent, UseInHandEvent>(OnUseInHand);
        SubscribeLocalEvent<ActivatableUIComponent, InteractUsingEvent>(OnInteractUsing);
        SubscribeLocalEvent<ActivatableUIComponent, HandDeselectedEvent>(OnHandDeselected);
        SubscribeLocalEvent<ActivatableUIComponent, GotUnequippedHandEvent>((uid, aui, _) => CloseAll(uid, aui));
        // *THIS IS A BLATANT WORKAROUND!* RATIONALE: Microwaves need it
        SubscribeLocalEvent<ActivatableUIComponent, EntParentChangedMessage>(OnParentChanged);
        SubscribeLocalEvent<ActivatableUIComponent, BoundUIClosedEvent>(OnUIClose);
        SubscribeLocalEvent<BoundUserInterfaceMessageAttempt>(OnBoundInterfaceInteractAttempt);

        SubscribeLocalEvent<ActivatableUIComponent, GetVerbsEvent<ActivationVerb>>(AddOpenUiVerb);

        SubscribeLocalEvent<UserInterfaceComponent, OpenUiActionEvent>(OnActionPerform);

        InitializePower();
    }

    private void OnBoundInterfaceInteractAttempt(BoundUserInterfaceMessageAttempt ev)
    {
        if (!TryComp(ev.Target, out ActivatableUIComponent? comp))
            return;

        if (!comp.RequireHands)
            return;

        if (!TryComp(ev.Actor, out HandsComponent? hands) || hands.Hands.Count == 0)
            ev.Cancel();
    }

    private void OnActionPerform(EntityUid uid, UserInterfaceComponent component, OpenUiActionEvent args)
    {
        if (args.Handled || args.Key == null)
            return;

        if (!TryComp(args.Performer, out ActorComponent? actor))
            return;

        args.Handled = _uiSystem.TryToggleUi(uid, args.Key, actor.PlayerSession);
    }

    private void AddOpenUiVerb(EntityUid uid, ActivatableUIComponent component, GetVerbsEvent<ActivationVerb> args)
    {
        if (!args.CanAccess)
            return;

        if (component.RequireHands && args.Hands == null)
            return;

        if (component.InHandsOnly && args.Using != uid)
            return;

        if (!args.CanInteract && (!component.AllowSpectator || !HasComp<GhostComponent>(args.User)))
            return;

        ActivationVerb verb = new();
        verb.Act = () => InteractUI(args.User, uid, component);
        verb.Text = Loc.GetString(component.VerbText);
        // TODO VERBS add "open UI" icon?
        args.Verbs.Add(verb);
    }

    private void OnActivate(EntityUid uid, ActivatableUIComponent component, ActivateInWorldEvent args)
    {
        if (args.Handled)
            return;

        if (component.InHandsOnly)
            return;

        if (component.AllowedItems != null)
            return;

        args.Handled = InteractUI(args.User, uid, component);
    }

    private void OnUseInHand(EntityUid uid, ActivatableUIComponent component, UseInHandEvent args)
    {
        if (args.Handled)
            return;

        if (component.RightClickOnly)
            return;

        if (component.AllowedItems != null)
            return;

        args.Handled = InteractUI(args.User, uid, component);
    }

    private void OnInteractUsing(EntityUid uid, ActivatableUIComponent component, InteractUsingEvent args)
    {
        if (args.Handled) return;
        if (component.AllowedItems == null) return;
        if (!component.AllowedItems.IsValid(args.Used, EntityManager)) return;
        args.Handled = InteractUI(args.User, uid, component);
    }

    private void OnParentChanged(EntityUid uid, ActivatableUIComponent aui, ref EntParentChangedMessage args)
    {
        CloseAll(uid, aui);
    }

    private void OnUIClose(EntityUid uid, ActivatableUIComponent component, BoundUIClosedEvent args)
    {
        var user = args.Actor;

        if (user != component.CurrentSingleUser)
            return;

        if (!Equals(args.UiKey, component.Key))
            return;

        SetCurrentSingleUser(uid, null, component);
    }

    private bool InteractUI(EntityUid user, EntityUid uiEntity, ActivatableUIComponent aui)
    {
        if (!_uiSystem.HasUi(uiEntity, aui.Key))
            return false;

        if (_uiSystem.IsUiOpen(uiEntity, aui.Key, user))
        {
            _uiSystem.CloseUi(uiEntity, aui.Key, user);
            return true;
        }

        if (!_blockerSystem.CanInteract(user, uiEntity) && (!aui.AllowSpectator || !HasComp<GhostComponent>(user)))
            return false;

        if (aui.RequireHands && !HasComp<HandsComponent>(user))
            return false;

        if (aui.AdminOnly && !_adminManager.IsAdmin(user))
            return false;

        if (aui.SingleUser && aui.CurrentSingleUser != null && user != aui.CurrentSingleUser)
        {
            string message = Loc.GetString("machine-already-in-use", ("machine", uiEntity));
            _popupSystem.PopupEntity(message, uiEntity, user);

            // If we get here, supposedly, the object is in use.
            // Check with BUI that it's ACTUALLY in use just in case.
            // Since this could brick the object if it goes wrong.
            if (_uiSystem.IsUiOpen(uiEntity, aui.Key))
                return false;
        }

        // If we've gotten this far, fire a cancellable event that indicates someone is about to activate this.
        // This is so that stuff can require further conditions (like power).
        var oae = new ActivatableUIOpenAttemptEvent(user);
        var uae = new UserOpenActivatableUIAttemptEvent(user, uiEntity);
        RaiseLocalEvent(user, uae);
        RaiseLocalEvent(uiEntity, oae);
        if (oae.Cancelled || uae.Cancelled)
            return false;

        // Give the UI an opportunity to prepare itself if it needs to do anything
        // before opening
        var bae = new BeforeActivatableUIOpenEvent(user);
        RaiseLocalEvent(uiEntity, bae);

        SetCurrentSingleUser(uiEntity, user, aui);
        _uiSystem.OpenUi(uiEntity, aui.Key, user);

        //Let the component know a user opened it so it can do whatever it needs to do
        var aae = new AfterActivatableUIOpenEvent(user, user);
        RaiseLocalEvent(uiEntity, aae);

        return true;
    }

    public void SetCurrentSingleUser(EntityUid uid, EntityUid? user, ActivatableUIComponent? aui = null)
    {
        if (!Resolve(uid, ref aui))
            return;

        if (!aui.SingleUser)
            return;

        aui.CurrentSingleUser = user;

        RaiseLocalEvent(uid, new ActivatableUIPlayerChangedEvent());
    }

    public void CloseAll(EntityUid uid, ActivatableUIComponent? aui = null)
    {
        if (!Resolve(uid, ref aui, false))
            return;

        if (!_uiSystem.HasUi(uid, aui.Key))
            return;

        _uiSystem.CloseUi(uid, aui.Key);
    }

    private void OnHandDeselected(EntityUid uid, ActivatableUIComponent? aui, HandDeselectedEvent args)
    {
        if (!Resolve(uid, ref aui, false))
            return;

        if (!aui.CloseOnHandDeselect)
            return;

        CloseAll(uid, aui);
    }
}
