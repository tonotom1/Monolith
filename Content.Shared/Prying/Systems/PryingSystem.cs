// SPDX-FileCopyrightText: 2023 Checkraze
// SPDX-FileCopyrightText: 2023 Pieter-Jan Briers
// SPDX-FileCopyrightText: 2023 ike709
// SPDX-FileCopyrightText: 2023 metalgearsloth
// SPDX-FileCopyrightText: 2024 DrSmugleaf
// SPDX-FileCopyrightText: 2024 MilenVolf
// SPDX-FileCopyrightText: 2024 Nemanja
// SPDX-FileCopyrightText: 2024 ShadowCommander
// SPDX-FileCopyrightText: 2024 nikthechampiongr
// SPDX-FileCopyrightText: 2025 Avalon
// SPDX-FileCopyrightText: 2025 mikusssssss
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Diagnostics.CodeAnalysis;
using Content.Shared.Administration.Logs;
using Content.Shared.Database;
using Content.Shared.DoAfter;
using Content.Shared.Doors.Components;
using Content.Shared.Interaction;
using Content.Shared.Popups;
using Content.Shared.Prying.Components;
using Content.Shared.Timing;
using Content.Shared.Verbs;
using Robust.Shared.Audio.Systems;
using Robust.Shared.Serialization;
using PryUnpoweredComponent = Content.Shared.Prying.Components.PryUnpoweredComponent;

namespace Content.Shared.Prying.Systems;

/// <summary>
/// Handles prying of entities (e.g. doors)
/// </summary>
public sealed class PryingSystem : EntitySystem
{
    [Dependency] private readonly ISharedAdminLogManager _adminLog = default!;
    [Dependency] private readonly SharedDoAfterSystem _doAfterSystem = default!;
    [Dependency] private readonly SharedAudioSystem _audioSystem = default!;
    [Dependency] private readonly SharedPopupSystem _popup = default!;
    [Dependency] private readonly UseDelaySystem _delay = default!; // Goobstation

    public override void Initialize()
    {
        base.Initialize();

        // Mob prying doors
        SubscribeLocalEvent<DoorComponent, GetVerbsEvent<AlternativeVerb>>(OnDoorAltVerb);
        SubscribeLocalEvent<DoorComponent, DoorPryDoAfterEvent>(OnDoAfter);
        SubscribeLocalEvent<DoorComponent, InteractUsingEvent>(TryPryDoor);
    }

    private void TryPryDoor(EntityUid uid, DoorComponent comp, InteractUsingEvent args)
    {
        if (args.Handled)
            return;

        args.Handled = TryPry(uid, args.User, out _, args.Used);
    }

    private void OnDoorAltVerb(EntityUid uid, DoorComponent component, GetVerbsEvent<AlternativeVerb> args)
    {
        if (!args.CanInteract || !args.CanAccess)
            return;

        if (!TryComp<PryingComponent>(args.User, out _))
            return;

        args.Verbs.Add(new AlternativeVerb()
        {
            Text = Loc.GetString("door-pry"),
            Impact = LogImpact.Low,
            Act = () => TryPry(uid, args.User, out _, args.User),
        });
    }

    /// <summary>
    /// Attempt to pry an entity.
    /// </summary>
    public bool TryPry(EntityUid target, EntityUid user, out DoAfterId? id, EntityUid tool)
    {
        id = null;

        if (TryComp(tool, out UseDelayComponent? delay) && _delay.IsDelayed((tool, delay))) // Goobstation
            return false;

        PryingComponent? comp = null;
        if (!Resolve(tool, ref comp, false))
            return false;

        if (!comp.Enabled)
            return false;

        if (!CanPry(target, user, out var message, comp))
        {
            if (!string.IsNullOrWhiteSpace(message))
                _popup.PopupClient(Loc.GetString(message), target, user);
            // If we have reached this point we want the event that caused this
            // to be marked as handled.
            return true;
        }

        StartPry(target, user, tool, comp.SpeedModifier, out id);

        return true;
    }

    /// <summary>
    /// Try to pry an entity.
    /// </summary>
    public bool TryPry(EntityUid target, EntityUid user, out DoAfterId? id)
    {
        id = null;

        if (TryComp(user, out UseDelayComponent? delay) && _delay.IsDelayed((user, delay))) // Goobstation
            return false;

        // We don't care about displaying a message if no tool was used.
        if (!TryComp<PryUnpoweredComponent>(target, out var unpoweredComp) || !CanPry(target, user, out _, unpoweredComp: unpoweredComp))
            // If we have reached this point we want the event that caused this
            // to be marked as handled.
            return true;

        // hand-prying is much slower
        var modifier = CompOrNull<PryingComponent>(user)?.SpeedModifier ?? unpoweredComp.PryModifier;
        return StartPry(target, user, user, modifier, out id); // Goob edit
    }

    private bool CanPry(EntityUid target, EntityUid user, out string? message, PryingComponent? comp = null, PryUnpoweredComponent? unpoweredComp = null)
    {
        BeforePryEvent canev;

        if (comp != null || Resolve(user, ref comp, false))
        {
            canev = new BeforePryEvent(user, comp.PryPowered, comp.Force, true);
        }
        else
        {
            if (!Resolve(target, ref unpoweredComp))
            {
                message = null;
                return false;
            }

            canev = new BeforePryEvent(user, false, false, false);
        }

        RaiseLocalEvent(target, ref canev);

        message = canev.Message;

        return !canev.Cancelled;
    }

    private bool StartPry(EntityUid target, EntityUid user, EntityUid? tool, float toolModifier, [NotNullWhen(true)] out DoAfterId? id)
    {
        var instaPry = TryComp(tool, out PryingComponent? prying) && prying.InstaPry; // Goobstation

        var modEv = new GetPryTimeModifierEvent(user, instaPry); // Goob edit

        RaiseLocalEvent(target, ref modEv);

        // Begin DeltaV additions
        // Also raise an event for users to modifiy the time to pry
        RaiseLocalEvent(user, ref modEv);
        // End DeltaV additions

        var doAfterArgs = new DoAfterArgs(EntityManager, user, TimeSpan.FromSeconds(modEv.BaseTime * modEv.PryTimeModifier / toolModifier), new DoorPryDoAfterEvent(), target, target, tool)
        {
            BreakOnDamage = true,
            BreakOnMove = true,
            NeedHand = tool != user,
        };

        if (tool != user && tool != null)
        {
            _adminLog.Add(LogType.Action, LogImpact.Low, $"{ToPrettyString(user)} is using {ToPrettyString(tool.Value)} to pry {ToPrettyString(target)}");
        }
        else
        {
            _adminLog.Add(LogType.Action, LogImpact.Low, $"{ToPrettyString(user)} is prying {ToPrettyString(target)}");
        }
        return _doAfterSystem.TryStartDoAfter(doAfterArgs, out id);
    }

    private void OnDoAfter(EntityUid uid, DoorComponent door, DoorPryDoAfterEvent args)
    {
        if (args.Cancelled)
            return;
        if (args.Target is null)
            return;

        TryComp<PryingComponent>(args.Used, out var comp);

        if (!CanPry(uid, args.User, out var message, comp))
        {
            if (!string.IsNullOrWhiteSpace(message))
                _popup.PopupClient(Loc.GetString(message), uid, args.User);
            return;
        }

        if (args.Used != null && comp != null)
        {
            _audioSystem.PlayPredicted(comp.UseSound, args.Used.Value, args.User);
        }

        var ev = new PriedEvent(args.User);
        RaiseLocalEvent(uid, ref ev);

        if (TryComp(args.Used, out UseDelayComponent? delay)) // Goobstation
            _delay.TryResetDelay((args.Used.Value, delay));
    }
}

[Serializable, NetSerializable]
public sealed partial class DoorPryDoAfterEvent : SimpleDoAfterEvent;
