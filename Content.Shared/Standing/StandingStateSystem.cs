// SPDX-FileCopyrightText: 2021 Acruid
// SPDX-FileCopyrightText: 2021 Clyybber
// SPDX-FileCopyrightText: 2021 Galactic Chimp
// SPDX-FileCopyrightText: 2021 Paul
// SPDX-FileCopyrightText: 2021 Vera Aguilera Puerto
// SPDX-FileCopyrightText: 2021 Visne
// SPDX-FileCopyrightText: 2022 Alex Evgrashin
// SPDX-FileCopyrightText: 2022 Fishfish458
// SPDX-FileCopyrightText: 2022 Francesco
// SPDX-FileCopyrightText: 2022 Jacob Tong
// SPDX-FileCopyrightText: 2022 Leon Friedrich
// SPDX-FileCopyrightText: 2022 fishfish458 <fishfish458>
// SPDX-FileCopyrightText: 2022 keronshb
// SPDX-FileCopyrightText: 2023 DrSmugleaf
// SPDX-FileCopyrightText: 2023 Pieter-Jan Briers
// SPDX-FileCopyrightText: 2024 Tayrtahn
// SPDX-FileCopyrightText: 2025 Ark
// SPDX-FileCopyrightText: 2025 SlamBamActionman
// SPDX-FileCopyrightText: 2025 metalgearsloth
//
// SPDX-License-Identifier: AGPL-3.0-or-later

// HEAVILY EDITED
// if wizden ever does something to this system we're FUCKED
// regards.

using Content.Shared._White;
using Content.Shared.Buckle;
using Content.Shared.Buckle.Components;
using Content.Shared.Hands.Components;
using Content.Shared.Movement.Systems;
using Content.Shared.Physics;
using Content.Shared.Rotation;
using Robust.Shared.Audio.Systems;
using Robust.Shared.Physics;
using Robust.Shared.Physics.Systems;

namespace Content.Shared.Standing;

public sealed class StandingStateSystem : EntitySystem
{
    [Dependency] private readonly SharedAppearanceSystem _appearance = default!;
    [Dependency] private readonly SharedAudioSystem _audio = default!;
    [Dependency] private readonly SharedPhysicsSystem _physics = default!;
    [Dependency] private readonly MovementSpeedModifierSystem _movement = default!; // WD EDIT
    [Dependency] private readonly SharedBuckleSystem _buckle = default!; // WD EDIT

    // If StandingCollisionLayer value is ever changed to more than one layer, the logic needs to be edited.
    private const int StandingCollisionLayer = (int) CollisionGroup.MidImpassable;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<StandingStateComponent, AttemptMobCollideEvent>(OnMobCollide);
        SubscribeLocalEvent<StandingStateComponent, AttemptMobTargetCollideEvent>(OnMobTargetCollide);
    }

    private void OnMobTargetCollide(Entity<StandingStateComponent> ent, ref AttemptMobTargetCollideEvent args)
    {
        if (!ent.Comp.Standing)
        {
            args.Cancelled = true;
        }
    }

    private void OnMobCollide(Entity<StandingStateComponent> ent, ref AttemptMobCollideEvent args)
    {
        if (!ent.Comp.Standing)
        {
            args.Cancelled = true;
        }
    }

    public bool IsDown(EntityUid uid, StandingStateComponent? standingState = null)
    {
        if (!Resolve(uid, ref standingState, false))
            return false;

        return standingState.CurrentState is StandingState.Lying or StandingState.GettingUp;
    }

    public bool Down(EntityUid uid,
        bool playSound = true,
        bool dropHeldItems = true,
        bool force = true,
        StandingStateComponent? standingState = null,
        AppearanceComponent? appearance = null,
        HandsComponent? hands = null)
    {
        // TODO: This should actually log missing comps...
        if (!Resolve(uid, ref standingState, false))
            return false;

        // Optional component.
        Resolve(uid, ref appearance, ref hands, false);

        if (standingState.CurrentState is StandingState.Lying or StandingState.GettingUp)
            return true;

        // This is just to avoid most callers doing this manually saving boilerplate
        // 99% of the time you'll want to drop items but in some scenarios (e.g. buckling) you don't want to.
        // We do this BEFORE downing because something like buckle may be blocking downing but we want to drop hand items anyway
        // and ultimately this is just to avoid boilerplate in Down callers + keep their behavior consistent.
        if (dropHeldItems && hands != null)
        {
            var ev = new DropHandItemsEvent();
            RaiseLocalEvent(uid, ref ev, false);
        }

        if (!force)
        {
            var msg = new DownAttemptEvent();
            RaiseLocalEvent(uid, msg, false);

            if (msg.Cancelled)
                return false;
        }

        standingState.CurrentState = StandingState.Lying;
        Dirty(uid, standingState);
        RaiseLocalEvent(uid, new DownedEvent(), false);

        // Seemed like the best place to put it
        _appearance.SetData(uid, RotationVisuals.RotationState, RotationState.Horizontal, appearance);

        // Change collision masks to allow going under certain entities like flaps and tables
        if (TryComp(uid, out FixturesComponent? fixtureComponent))
        {
            foreach (var (key, fixture) in fixtureComponent.Fixtures)
            {
                if ((fixture.CollisionMask & StandingCollisionLayer) == 0)
                    continue;

                standingState.ChangedFixtures.Add(key);
                _physics.SetCollisionMask(uid, key, fixture, fixture.CollisionMask & ~StandingCollisionLayer, manager: fixtureComponent);
            }
        }
        // check if component was just added or streamed to client
        // if true, no need to play sound - mob was down before player could seen that
        if (standingState.LifeStage <= ComponentLifeStage.Starting)
            return true;

        if (playSound)
        {
            _audio.PlayPredicted(standingState.DownSound, uid, null);
        }

        _movement.RefreshMovementSpeedModifiers(uid); // WD EDIT
        return true;
    }

    public bool Stand(EntityUid uid,
        StandingStateComponent? standingState = null,
        AppearanceComponent? appearance = null,
        bool force = false)
    {
        // TODO: This should actually log missing comps...
        if (!Resolve(uid, ref standingState, false))
            return false;
        // Optional component.
        Resolve(uid, ref appearance, false);

        if (standingState.CurrentState is StandingState.Standing)
            return true;

        if (TryComp(uid, out BuckleComponent? buckle) && buckle.Buckled && !_buckle.TryUnbuckle(uid, uid, buckleComp: buckle)) // WD EDIT
            return false;

        if (!force)
        {
            var msg = new StandAttemptEvent();
            RaiseLocalEvent(uid, msg, false);
            if (msg.Cancelled)
                return false;
        }

        standingState.CurrentState = StandingState.Standing;
        Dirty(uid, standingState);
        RaiseLocalEvent(uid, new StoodEvent(), false);

        _appearance.SetData(uid, RotationVisuals.RotationState, RotationState.Vertical, appearance);
        if (TryComp(uid, out FixturesComponent? fixtureComponent))
        {
            foreach (var key in standingState.ChangedFixtures)
            {
                if (fixtureComponent.Fixtures.TryGetValue(key, out var fixture))
                    _physics.SetCollisionMask(uid, key, fixture, fixture.CollisionMask | StandingCollisionLayer, fixtureComponent);
            }
        }
        standingState.ChangedFixtures.Clear();
        _movement.RefreshMovementSpeedModifiers(uid); // WD EDIT

        return true;
    }
}

[ByRefEvent]
public record struct DropHandItemsEvent();

/// <summary>
/// Subscribe if you can potentially block a down attempt.
/// </summary>
public sealed class DownAttemptEvent : CancellableEntityEventArgs
{
}

/// <summary>
/// Subscribe if you can potentially block a stand attempt.
/// </summary>
public sealed class StandAttemptEvent : CancellableEntityEventArgs
{
}

/// <summary>
/// Raised when an entity becomes standing
/// </summary>
public sealed class StoodEvent : EntityEventArgs
{
}

/// <summary>
/// Raised when an entity is not standing
/// </summary>
public sealed class DownedEvent : EntityEventArgs
{
}

/// <summary>
/// Raised after an entity falls down.
/// </summary>
public sealed class FellDownEvent : EntityEventArgs
{
    public EntityUid Uid { get; }

    public FellDownEvent(EntityUid uid)
    {
        Uid = uid;
    }
}

/// <summary>
/// Raised on the entity being thrown due to the holder falling down.
/// </summary>
[ByRefEvent]
public record struct FellDownThrowAttemptEvent(EntityUid Thrower, bool Cancelled = false);


