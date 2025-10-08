using Content.Shared.Actions;
using Content.Shared._Europa.Morph;
using Content.Shared.Weapons.Melee.Events;
using Content.Shared.Mobs.Systems;
using Content.Shared.Nutrition.Components;
using Content.Shared.Nutrition.EntitySystems;
using Content.Shared.DoAfter;
using Robust.Shared.Player;
using Robust.Shared.Random;
using Content.Shared.Humanoid;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Components;
using Content.Shared.Popups;
using Content.Shared.Whitelist;
using Robust.Shared.Audio.Systems;
using Robust.Shared.Containers;
using Content.Shared.Damage;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Hands.Components;
using System.Linq;
using System.Numerics;
using Content.Shared.Examine;
using Robust.Shared.Prototypes;
using Content.Shared.Damage.Prototypes;
using Content.Shared.Polymorph.Components;
using Content.Shared.Interaction;
using Content.Shared.Polymorph.Systems;
using Content.Server.Chat.Systems;
using Content.Server.Stunnable;
using Content.Shared.Tools.Systems;
using Content.Shared.Tools.Components;
using Content.Shared.ActionBlocker;
using Content.Shared.Movement.Events;
using Content.Shared.Movement.Components;
using Content.Shared.Standing;
using Content.Server.Ghost.Roles.Components;
using Content.Shared.Body.Events;
using Content.Shared.Ghost;
using Content.Shared.Mind.Components;
using Robust.Server.GameObjects;
using Robust.Shared.Map;

namespace Content.Server._Europa.Morph;

//
// License-Identifier: AGPL-3.0-or-later
//

public sealed class MorphSystem : SharedMorphSystem
{
    [Dependency] private readonly ActionBlockerSystem _actionBlocker = default!;
    [Dependency] private readonly ChatSystem _chatSystem = default!;
    [Dependency] private readonly SharedActionsSystem _actions = default!;
    [Dependency] private readonly SharedChameleonProjectorSystem _chameleon = default!;
    [Dependency] private readonly SharedContainerSystem _container = default!;
    [Dependency] private readonly DamageableSystem _damageable = default!;
    [Dependency] private readonly SharedHandsSystem _hands = default!;
    [Dependency] private readonly IRobustRandom _random = default!;
    [Dependency] private readonly HungerSystem _hunger = default!;
    [Dependency] private readonly SharedAudioSystem _audioSystem = default!;
    [Dependency] private readonly SharedDoAfterSystem _doAfterSystem = default!;
    [Dependency] private readonly SharedPopupSystem _popup = default!;
    [Dependency] private readonly EntityWhitelistSystem _whitelistSystem = default!;
    [Dependency] private readonly MobThresholdSystem _threshold = default!;
    [Dependency] private readonly IPrototypeManager _proto = default!;
    [Dependency] private readonly SharedUserInterfaceSystem _ui = default!;
    [Dependency] private readonly StunSystem _stun = default!;
    [Dependency] private readonly WeldableSystem _weldable = default!;
    [Dependency] private readonly StandingStateSystem _standing = default!;
    [Dependency] private readonly TransformSystem _transform = default!;
    [Dependency] private readonly EntityLookupSystem _lookup = default!;
    [Dependency] private readonly MobStateSystem _mobState = default!;

    public ProtoId<DamageGroupPrototype> BruteDamageGroup = "Brute";
    public ProtoId<DamageGroupPrototype> BurnDamageGroup = "Burn";

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<MorphComponent, AttackedEvent>(OnAttacked);
        SubscribeLocalEvent<MorphComponent, MeleeHitEvent>(OnAttack);

        SubscribeLocalEvent<MorphComponent, MapInitEvent>(OnInit);
        SubscribeLocalEvent<MorphComponent, BeingGibbedEvent>(OnDestroy);
        SubscribeLocalEvent<MorphComponent, ExaminedEvent>(OnExamined);
        SubscribeLocalEvent<MorphComponent, InteractHandEvent>(OnInteract);

        SubscribeLocalEvent<MorphComponent, MorphOpenRadialMenuEvent>(OnMimicryRadialMenu);
        SubscribeLocalEvent<MorphComponent, EventMimicryActivate>(OnMimicryActivate);
        SubscribeLocalEvent<MorphComponent, MorphDevourActionEvent>(OnDevourAction);
        SubscribeLocalEvent<MorphComponent, MorphReproduceActionEvent>(OnReproduceAction);
        SubscribeLocalEvent<MorphComponent, MorphMimicryRememberActionEvent>(OnMimicryRememberAction);
        SubscribeLocalEvent<MorphComponent, MorphVentOpenActionEvent>(OnOpenVentAction);

        SubscribeLocalEvent<MorphAmbushComponent, MeleeHitEvent>(OnAmbushAttack);
        SubscribeLocalEvent<MorphAmbushComponent, UndisguisedEvent>(OnAmbushInteract);
        SubscribeLocalEvent<MorphComponent, MorphAmbushActionEvent>(OnAmbushAction);
        SubscribeLocalEvent<MorphAmbushComponent, UpdateCanMoveEvent>(OnCanMoveEvent);

        SubscribeLocalEvent<MorphComponent, MorphDevourDoAfterEvent>(OnDoDevourAfter);
    }

    private void OnDestroy(EntityUid uid, MorphComponent component, ref BeingGibbedEvent args)
    {
        foreach (var entity in component.ContainedCreatures)
        {
            var transform = Transform(uid);
            _transform.SetCoordinates(entity, transform.Coordinates);
        }
    }

    private void OnInit(EntityUid uid, MorphComponent component, MapInitEvent args)
    {
        _actions.AddAction(uid, ref component.DevourActionEntity, component.DevourAction);
        _actions.AddAction(uid, ref component.MemoryActionEntity, component.MemoryAction);
        _actions.AddAction(uid, ref component.MimicryActionEntity, component.MimicryAction);
        _actions.AddAction(uid, ref component.ReplicationActionEntity, component.ReplicationAction);
        _actions.AddAction(uid, ref component.AmbushActionEntity, component.AmbushAction);
        _actions.AddAction(uid, ref component.VentOpenActionEntity, component.VentOpenAction);
    }

    private void OnAttacked(Entity<MorphComponent> ent, ref AttackedEvent args)
    {
        if (!TryComp<HungerComponent>(ent, out var hunger))
            return;

        if (args.User == args.Used)
        {
            _damageable.TryChangeDamage(args.User, ent.Comp.DamageOnTouch);
            _hunger.ModifyHunger(ent, ent.Comp.EatWeaponHungerReq, hunger);
        }
        else if (_random.Prob(ent.Comp.EatWeaponChanceOnHited) && _hunger.GetHunger(hunger) >= ent.Comp.EatWeaponHungerReq)
        {
            ent.Comp.ContainedCreatures.Add(args.Used);
            _transform.SetCoordinates(args.Used, new EntityCoordinates(EntityUid.Invalid, Vector2.Zero));
            _audioSystem.PlayPvs(ent.Comp.SoundDevour, ent);
            _hunger.ModifyHunger(ent, -ent.Comp.EatWeaponHungerReq, hunger);
        }
    }

    private void OnAttack(Entity<MorphComponent> ent, ref MeleeHitEvent args)
    {
        _chameleon.TryReveal(ent.Owner);

        if (args.HitEntities.Count <= 0)
            return;

        if (!TryComp<HandsComponent>(args.HitEntities[0], out var hands))
            return;

        if (!TryComp<HungerComponent>(ent, out var hunger))
            return;

        if (_hands.TryGetActiveItem((args.HitEntities[0], hands), out var item) && _random.Prob(ent.Comp.EatWeaponChanceOnHit))
        {
            if (_hunger.GetHunger(hunger) < ent.Comp.EatWeaponHungerReq)
                return;

            ent.Comp.ContainedCreatures.Add(item.Value);
            _transform.SetCoordinates(item.Value, new EntityCoordinates(EntityUid.Invalid, Vector2.Zero));
            _audioSystem.PlayPvs(ent.Comp.SoundDevour, ent);
            _hunger.ModifyHunger(ent, -ent.Comp.EatWeaponHungerReq, hunger);
        }
    }

    private void OnInteract(Entity<MorphComponent> ent, ref InteractHandEvent args)
    {
        _chameleon.TryReveal(ent.Owner);
    }

    private void OnOpenVentAction(EntityUid uid, MorphComponent comp, MorphVentOpenActionEvent args)
    {
        if (!TryComp<HungerComponent>(uid, out var hunger))
            return;

        if (_container.IsEntityInContainer(uid))
            return;

        if (_hunger.GetHunger(hunger) < comp.OpenVentFoodReq)
            return;

        if (!TryComp<WeldableComponent>(args.Target, out var weldableComponent) || !weldableComponent.IsWelded)
            return;

        _hunger.ModifyHunger(uid, -comp.OpenVentFoodReq, hunger);
        _weldable.SetWeldedState(args.Target, false, weldableComponent);
        _popup.PopupEntity(Loc.GetString("morph-vent-action-success", ("target", ToPrettyString(args.Target))), uid, PopupType.Medium);
    }

    private void OnExamined(EntityUid uid, MorphComponent comp, ExaminedEvent args)
    {
        if (!TryComp<HungerComponent>(uid, out var hunger))
            return;

        if (args.Examiner != uid)
            return;

        var hungerCount = _hunger.GetHunger(hunger);
        args.PushMarkup($"[color=yellow]{Loc.GetString("comp-morph-examined-hunger", ("hunger", hungerCount))}[/color]");
    }

    private void OnMimicryActivate(EntityUid uid, MorphComponent component, EventMimicryActivate args)
    {
        if (!TryComp<ChameleonProjectorComponent>(uid, out var chamel))
            return;

        var targ = GetEntity(args.Target);

        if (targ != null)
            MimicryNonHumanoid((uid, chamel), targ.Value);
    }

    private void OnAmbushAction(EntityUid uid, MorphComponent component, MorphAmbushActionEvent args)
    {
        if (!TryComp<ChameleonProjectorComponent>(uid, out var chamel))
            return;

        if (NonMorphInRange(uid, component))
        {
            _popup.PopupCursor(Loc.GetString("morph-ambush-blocked"), uid);
            return;
        }

        if (TryComp<MorphAmbushComponent>(uid, out _))
        {
            AmbushBreak(uid);
            if (chamel.Disguised != null)
                AmbushBreak(chamel.Disguised.Value);
        }
        else
        {
            EnsureComp<MorphAmbushComponent>(uid);
            _popup.PopupCursor(Loc.GetString("morphs-into-ambush"), uid);

            if (TryComp<ChameleonDisguisedComponent>(uid, out var disgui))
                EnsureComp<MorphAmbushComponent>(disgui.Disguise);
            _actionBlocker.UpdateCanMove(uid);
        }
    }

    private void OnCanMoveEvent(EntityUid uid, MorphAmbushComponent component, UpdateCanMoveEvent args)
    {
        args.Cancel();
    }

    private void OnAmbushAttack(Entity<MorphAmbushComponent> ent, ref MeleeHitEvent args)
    {
        _standing.Down(args.HitEntities[0]);
        AmbushBreak(ent);
    }

    public void AmbushBreak(EntityUid uid)
    {
        if (!HasComp<MorphAmbushComponent>(uid))
            return;

        _popup.PopupCursor(Loc.GetString("morphs-out-of-ambush"), uid);
        RemCompDeferred<MorphAmbushComponent>(uid);

        if (TryComp<MorphComponent>(uid, out var morph))
        {
            _chameleon.TryReveal(uid);
            _actions.StartUseDelay(morph.AmbushActionEntity);
        }

        if (TryComp<ChameleonProjectorComponent>(uid, out var chamel) && chamel.Disguised != null)
            RemCompDeferred<MorphAmbushComponent>(chamel.Disguised.Value);

        if (TryComp<InputMoverComponent>(uid, out var input))
        {
            input.CanMove = true;
            Dirty(uid, input);
        }
    }

    private bool NonMorphInRange(EntityUid uid, MorphComponent component)
    {
        var coordinates = _transform.GetMapCoordinates(uid);
        foreach (var entity in _lookup.GetEntitiesInRange(coordinates, component.AmbushBlockRange))
        {
            if (HasComp<MindContainerComponent>(entity) && !HasComp<MorphComponent>(entity) && !HasComp<GhostComponent>(entity))
            {
                if ((TryComp<MobStateComponent>(entity, out var entityMobState) && HasComp<GhostTakeoverAvailableComponent>(entity) && _mobState.IsDead(entity, entityMobState)))
                    continue;

                return true;
            }
        }

        return false;
    }

    private void OnAmbushInteract(EntityUid uid, MorphAmbushComponent component, UndisguisedEvent args)
    {
        _stun.TryParalyze(args.User, component.StunTimeInteract, false);
        _damageable.TryChangeDamage(args.User, component.DamageOnTouch);
        AmbushBreak(uid);
    }

    private void OnMimicryRadialMenu(EntityUid uid, MorphComponent component, MorphOpenRadialMenuEvent args)
    {
        component.MimicryContainer = _container.EnsureContainer<Container>(uid, component.MimicryContainerId);

        if (!TryComp<UserInterfaceComponent>(uid, out var uic))
            return;

        _ui.OpenUi((uid, uic), MimicryKey.Key, uid);
        _chameleon.TryReveal(uid);
    }

    private void OnMimicryRememberAction(EntityUid uid, MorphComponent component, MorphMimicryRememberActionEvent args)
    {
        if (!TryComp<ChameleonProjectorComponent>(uid, out var chamel))
            return;

        //отвечает за запоминание энтити для мимикрии.
        //гуманоидов запоминает отдельно т.к. их невозможно показать путём хамелеона
        //короче мне лень эту хреноетнь выписывать. Кто будет её чинить - мои соболезнования вам
        if (TryComp<HumanoidAppearanceComponent>(args.Target, out _))
        {
            //короче мне лень эту хреноетнь выписывать. Кто будет её чинить - мои соболезнования вам
            //TODO: сделать морфабильность гуманоидов. Этот метод работает, но на 50%. Он спавнит зуманоида и устанавливает ему вид, но не может прицепить его
            //вероятно, беды в прототипах
            // var transform = Transform(uid);
            // var target = SpawnAttachedTo("MorphHumanoidDummy", transform.Coordinates);
            // if (!TryComp<HumanoidAppearanceComponent>(target, out var targethumanoid))
            //     return;
            // component.ApperanceList.Add(humanoid);
            // if (component.ApperanceList.Count() > 5) component.ApperanceList.RemoveAt(0);
            // _humanoid.SetAppearance(component.ApperanceList[0], targethumanoid);
        }
        else
        {
            if (_chameleon.IsInvalid(chamel, args.Target))
            {
                _popup.PopupCursor(Loc.GetString("morph-unable-to-remember"), uid);
                return;
            }

            if (component.MemoryObjects.Count() > 5)
            {
                component.MemoryObjects.RemoveAt(0);
            }

            component.MemoryObjects.Add(args.Target);
            _popup.PopupEntity(
                Loc.GetString("morph-remember-action-success", ("target", ToPrettyString(args.Target))),
                uid,
                PopupType.Medium
            );
        }

        Dirty(uid, component);
    }

    //сюда надо перенести части из метода выше, а пока этот метод в комментах
    // public void MimicryHumanoid(EntityUid morph, EntityUid humanoid, HumanoidAppearanceComponent apperance)
    // {

    // }

    public void MimicryNonHumanoid(Entity<ChameleonProjectorComponent> morph, EntityUid toChameleon)
    {
        if (!Exists(toChameleon) || Deleted(toChameleon))
            return;

        _chameleon.Disguise(morph, morph, toChameleon);
    }

    private void OnDevourAction(EntityUid uid, MorphComponent component, MorphDevourActionEvent args)
    {
        if (args.Handled)
            return;

        if (_whitelistSystem.IsWhitelistFailOrNull(component.DevourWhitelist, args.Target))
            return;

        if (_whitelistSystem.IsWhitelistPassOrNull(component.DevourBlacklist, args.Target))
        {
            _popup.PopupEntity(Loc.GetString("devour-action-popup-message-blacklisted", ("target", ToPrettyString(args.Target))), uid, uid);
            return;
        }

        args.Handled = true;
        var target = args.Target;
        AmbushBreak(uid);

        if (TryComp(target, out MobStateComponent? targetState))
        {
            switch (targetState.CurrentState)
            {
                case MobState.Critical:
                    _popup.PopupEntity(Loc.GetString("devour-action-popup-message-fail-target-alive"), uid, uid);
                    break;
                case MobState.Dead:

                    _doAfterSystem.TryStartDoAfter(new DoAfterArgs(EntityManager, uid, component.DevourTime, new MorphDevourDoAfterEvent(), uid, target: target, used: uid)
                    {
                        BreakOnMove = true,
                    });
                    break;
                default:
                    _popup.PopupEntity(Loc.GetString("devour-action-popup-message-fail-target-alive"), uid, uid);
                    break;
            }

            return;
        }

        _popup.PopupEntity(Loc.GetString("devour-action-popup-message-structure"), uid, uid);
        _doAfterSystem.TryStartDoAfter(new DoAfterArgs(EntityManager, uid, component.DevourTime / 2, new MorphDevourDoAfterEvent(), uid, target: target, used: uid)
        {
            BreakOnMove = true,
        });
    }

    private void OnReproduceAction(EntityUid uid, MorphComponent component, MorphReproduceActionEvent args)
    {
        if (!TryComp<HungerComponent>(uid, out var hunger))
            return;

        if (_hunger.GetHunger(hunger) >= component.ReplicationFoodReq)
        {
            Spawn(component.MorphSpawnProto, Transform(uid).Coordinates);
            _hunger.ModifyHunger(uid, -component.ReplicationFoodReq, hunger);

            var morphList = new List<EntityUid>();
            var morphs = AllEntityQuery<MorphComponent, MobStateComponent>();
            while (morphs.MoveNext(out var ent, out _, out _))
            {
                morphList.Add(ent);
            }

            if (morphList.Count() == component.DetectableCount)
            {
                _chatSystem.DispatchFilteredAnnouncement(Filter.Broadcast(), Loc.GetString("morphs-announcement"), playSound: false, colorOverride: Color.Gold);
                _audioSystem.PlayGlobal(component.SoundReplication, Filter.Broadcast(), true);
            }

            _actions.StartUseDelay(component.ReplicationActionEntity);
        }
    }

    private void OnDoDevourAfter(EntityUid uid, MorphComponent component, MorphDevourDoAfterEvent args)
    {
        if (args.Handled || args.Cancelled || args.Target == null)
            return;

        if (!TryComp<HungerComponent>(uid, out var hunger))
            return;

        // Item devour
        if (!TryComp<MobThresholdsComponent>(args.Target, out var state) || !_threshold.TryGetDeadThreshold(args.Target.Value, out var health))
        {
            if (_hunger.GetHunger(hunger) < component.EatNonOrganicFoodReq)
            {
                _popup.PopupEntity(Loc.GetString("devour-action-popup-message-not-enough-hunger"), uid, uid);
                return;
            }
            _hunger.ModifyHunger(uid, -component.EatNonOrganicFoodReq, hunger);
            _audioSystem.PlayPvs(component.SoundDevour, uid);
            component.ContainedCreatures.Add(args.Target.Value);
            _transform.SetCoordinates(args.Target.Value, new EntityCoordinates(EntityUid.Invalid, Vector2.Zero));
            return;
        }

        if (state.CurrentThresholdState != MobState.Dead)
            return;

        if (!HasComp<HumanoidAppearanceComponent>(args.Target))
            health /= 2;

        var damageBrute = new DamageSpecifier(_proto.Index(BruteDamageGroup), -health.Value / 2);
        var damageBurn = new DamageSpecifier(_proto.Index(BurnDamageGroup), -health.Value / 2);

        _damageable.TryChangeDamage(uid, damageBrute);
        _damageable.TryChangeDamage(uid, damageBurn);
        _hunger.ModifyHunger(uid, (int)Math.Abs((float)health.Value / 3.5f), hunger);
        _audioSystem.PlayPvs(component.SoundDevour, uid);
        component.ContainedCreatures.Add(args.Target.Value);
        _transform.SetCoordinates(args.Target.Value, new EntityCoordinates(EntityUid.Invalid, Vector2.Zero));
    }
}
