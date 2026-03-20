// SPDX-FileCopyrightText: 2022 Flipp Syder <76629141+vulppine@users.noreply.github.com>
// SPDX-FileCopyrightText: 2022 metalgearsloth <comedian_vs_clown@hotmail.com>
// SPDX-FileCopyrightText: 2023 Artjom <artjombebenin@gmail.com>
// SPDX-FileCopyrightText: 2023 DrSmugleaf <DrSmugleaf@users.noreply.github.com>
// SPDX-FileCopyrightText: 2023 Pieter-Jan Briers <pieterjan.briers+git@gmail.com>
// SPDX-FileCopyrightText: 2023 TemporalOroboros <TemporalOroboros@gmail.com>
// SPDX-FileCopyrightText: 2024 deltanedas <39013340+deltanedas@users.noreply.github.com>
// SPDX-FileCopyrightText: 2024 metalgearsloth <31366439+metalgearsloth@users.noreply.github.com>
// SPDX-FileCopyrightText: 2024 nikthechampiongr <32041239+nikthechampiongr@users.noreply.github.com>
// SPDX-FileCopyrightText: 2025 Aiden <28298836+Aidenkrz@users.noreply.github.com>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Linq;
using Content.Server.Station.Systems;
using Content.Server.StationRecords.Components;
using Content.Shared.CriminalRecords;
using Content.Shared.Customization.Systems;
using Content.Shared.Humanoid;
using Content.Shared.Humanoid.Prototypes;
using Content.Shared.Preferences;
using Content.Shared.Roles;
using Content.Shared.StationRecords;
using Content.Shared._Pirate.Contractors.Prototypes;
using Robust.Server.GameObjects;
using Robust.Shared.Enums;
using Robust.Shared.Prototypes;

namespace Content.Server.StationRecords.Systems;

public sealed class GeneralStationRecordConsoleSystem : EntitySystem
{
    [Dependency] private readonly UserInterfaceSystem _ui = default!;
    [Dependency] private readonly StationSystem _station = default!;
    [Dependency] private readonly StationRecordsSystem _stationRecords = default!;
    #region Pirate: general/security record decoupling
    [Dependency] private readonly IPrototypeManager _prototypeManager = default!;
    private static readonly IReadOnlyDictionary<string, TimeSpan> EmptyPlayTimes = new Dictionary<string, TimeSpan>();
    #endregion

    public override void Initialize()
    {
        SubscribeLocalEvent<GeneralStationRecordConsoleComponent, RecordModifiedEvent>(UpdateUserInterface);
        SubscribeLocalEvent<GeneralStationRecordConsoleComponent, AfterGeneralRecordCreatedEvent>(UpdateUserInterface);
        SubscribeLocalEvent<GeneralStationRecordConsoleComponent, RecordRemovedEvent>(UpdateUserInterface);

        Subs.BuiEvents<GeneralStationRecordConsoleComponent>(GeneralStationRecordConsoleKey.Key, subs =>
        {
            subs.Event<BoundUIOpenedEvent>(UpdateUserInterface);
            subs.Event<SelectStationRecord>(OnKeySelected);
            subs.Event<SetStationRecordFilter>(OnFiltersChanged);
            #region Pirate: general/security record decoupling
            subs.Event<GeneralRecordCreateRecord>(OnCreateRecord);
            subs.Event<GeneralRecordEditIdentity>(OnEditIdentity);
            subs.Event<GeneralRecordEditForensics>(OnEditForensics);
            #endregion
            subs.Event<DeleteStationRecord>(OnRecordDelete);
        });
    }

    private void OnRecordDelete(Entity<GeneralStationRecordConsoleComponent> ent, ref DeleteStationRecord args)
    {
        if (!ent.Comp.CanDeleteEntries)
            return;

        var owning = _station.GetOwningStation(ent.Owner);
        if (owning == null)
            return;

        var key = new StationRecordKey(args.Id, owning.Value);
        if (!_stationRecords.TryGetRecord<GeneralStationRecord>(key, out var generalRecord))
            return;

        // Pirate: general/security record decoupling
        if (_stationRecords.TryGetRecord<CriminalRecord>(key, out var criminalRecord))
        {
            criminalRecord.GeneralRecordSnapshot = generalRecord with { };
            _stationRecords.RemoveRecordEntry<GeneralStationRecord>(key);
        }
        else
        {
            _stationRecords.RemoveRecord(key);
        }

        if (ent.Comp.ActiveKey == args.Id)
            ent.Comp.ActiveKey = null;

        UpdateUserInterface(ent);
    }

    private void UpdateUserInterface<T>(Entity<GeneralStationRecordConsoleComponent> ent, ref T args)
    {
        UpdateUserInterface(ent);
    }

    private void OnKeySelected(Entity<GeneralStationRecordConsoleComponent> ent, ref SelectStationRecord msg)
    {
        ent.Comp.ActiveKey = msg.SelectedKey;
        UpdateUserInterface(ent);
    }

    private void OnFiltersChanged(Entity<GeneralStationRecordConsoleComponent> ent, ref SetStationRecordFilter msg)
    {
        if (ent.Comp.Filter == null ||
            ent.Comp.Filter.Type != msg.Type || ent.Comp.Filter.Value != msg.Value)
        {
            ent.Comp.Filter = new StationRecordsFilter(msg.Type, msg.Value);
            UpdateUserInterface(ent);
        }
    }

    #region Pirate: general/security record decoupling
    private void OnCreateRecord(Entity<GeneralStationRecordConsoleComponent> ent, ref GeneralRecordCreateRecord msg)
    {
        if (!ent.Comp.CanDeleteEntries)
            return;

        if (_station.GetOwningStation(ent) is not { } station ||
            !TryComp<StationRecordsComponent>(station, out var stationRecords))
        {
            return;
        }

        var name = msg.Name.Trim();
        if (string.IsNullOrWhiteSpace(name) || name.Length > ent.Comp.MaxStringLength)
            return;

        var generalMatches = _stationRecords.GetRecordIdsByName(station, name, stationRecords);
        if (generalMatches.Count > 1)
            return;

        if (generalMatches.Count == 1)
        {
            ent.Comp.ActiveKey = generalMatches[0];
            UpdateUserInterface(ent);
            return;
        }

        var criminalMatches = _stationRecords.GetRecordsOfType<CriminalRecord>(station, stationRecords)
            .Where(pair => !_stationRecords.TryGetRecord<GeneralStationRecord>(new StationRecordKey(pair.Item1, station), out _)
                && pair.Item2.GeneralRecordSnapshot?.Name == name)
            .ToList();

        if (criminalMatches.Count > 1)
            return;

        StationRecordKey key;
        if (criminalMatches.Count == 1)
        {
            var (recordId, criminalRecord) = criminalMatches[0];
            key = new StationRecordKey(recordId, station);
            var generalRecord = criminalRecord.GeneralRecordSnapshot is { } snapshot
                ? snapshot with { }
                : CreateDefaultGeneralRecord(name);

            _stationRecords.AddRecordEntry(key, generalRecord, stationRecords);
            criminalRecord.GeneralRecordSnapshot = generalRecord with { };
            _stationRecords.Synchronize(key, stationRecords);
        }
        else
        {
            var generalRecord = CreateDefaultGeneralRecord(name);
            key = _stationRecords.AddRecordEntry(station, generalRecord, stationRecords);
            if (!key.IsValid())
                return;

            _stationRecords.Synchronize(key, stationRecords);
        }

        ent.Comp.ActiveKey = key.Id;
        UpdateUserInterface(ent);
    }

    private void OnEditIdentity(Entity<GeneralStationRecordConsoleComponent> ent, ref GeneralRecordEditIdentity msg)
    {
        if (_station.GetOwningStation(ent) is not { } station || ent.Comp.ActiveKey is not { } id)
            return;

        var key = new StationRecordKey(id, station);
        if (!_stationRecords.TryGetRecord<GeneralStationRecord>(key, out var generalRecord))
            return;

        if (!_prototypeManager.TryIndex<SpeciesPrototype>(msg.Species, out var speciesProto)
            || !speciesProto.RoundStart)
        {
            return;
        }

        _stationRecords.TryGetRecord<CriminalRecord>(key, out var criminalRecord);

        var age = Math.Clamp(msg.Age, speciesProto.MinAge, speciesProto.MaxAge);
        var profile = BuildEditedProfile(generalRecord, criminalRecord, speciesProto.ID, age, msg.Gender);
        var nationality = NormalizeNationality(msg.Nationality, profile);
        profile = profile.WithNationality(nationality);
        var employer = NormalizeEmployer(msg.Employer, profile);
        profile = profile.WithEmployer(employer);

        generalRecord.Species = speciesProto.ID;
        generalRecord.Age = age;
        generalRecord.Gender = msg.Gender;
        generalRecord.Nationality = nationality;
        generalRecord.Employer = employer;

        if (criminalRecord != null)
        {
            criminalRecord.GeneralRecordSnapshot = generalRecord with { };

            if (criminalRecord.PortraitProfileSnapshot != null || criminalRecord.PortraitImageData is { Length: > 0 })
                criminalRecord.PortraitProfileSnapshot = profile;
        }

        _stationRecords.Synchronize(key);
    }

    private void OnEditForensics(Entity<GeneralStationRecordConsoleComponent> ent, ref GeneralRecordEditForensics msg)
    {
        if (_station.GetOwningStation(ent) is not { } station || ent.Comp.ActiveKey is not { } id)
            return;

        var key = new StationRecordKey(id, station);
        if (!_stationRecords.TryGetRecord<GeneralStationRecord>(key, out var generalRecord))
            return;

        var fingerprint = msg.Fingerprint.Trim();
        var dna = msg.Dna.Trim();
        if (fingerprint.Length > ent.Comp.MaxStringLength || dna.Length > ent.Comp.MaxStringLength)
            return;

        generalRecord.Fingerprint = string.IsNullOrWhiteSpace(fingerprint)
            ? null
            : fingerprint;
        generalRecord.DNA = string.IsNullOrWhiteSpace(dna)
            ? null
            : dna;

        if (_stationRecords.TryGetRecord<CriminalRecord>(key, out var criminalRecord))
            criminalRecord.GeneralRecordSnapshot = generalRecord with { };

        _stationRecords.Synchronize(key);
    }

    private GeneralStationRecord CreateDefaultGeneralRecord(string name)
    {
        return new GeneralStationRecord
        {
            Name = name,
            Age = 18,
            JobTitle = Loc.GetString("suit-sensor-component-unknown-job"),
            JobIcon = string.Empty,
            JobPrototype = string.Empty,
            Nationality = string.Empty,
            Employer = string.Empty,
            Species = string.Empty,
            Gender = Gender.Male,
            DisplayPriority = 0,
            Fingerprint = null,
            DNA = null,
        };
    }

    private HumanoidCharacterProfile BuildEditedProfile(
        GeneralStationRecord generalRecord,
        CriminalRecord? criminalRecord,
        string species,
        int age,
        Gender gender)
    {
        var portraitSnapshot = criminalRecord?.PortraitProfileSnapshot;
        var sameSpeciesSnapshot = portraitSnapshot != null && portraitSnapshot.Species == species;

        var profile = sameSpeciesSnapshot
            ? new HumanoidCharacterProfile(portraitSnapshot!)
            : HumanoidCharacterProfile.DefaultWithSpecies(species).WithName(generalRecord.Name);

        return profile
            .WithSpecies(species)
            .WithAge(age)
            .WithGender(gender)
            .WithNationality(generalRecord.Nationality)
            .WithEmployer(generalRecord.Employer);
    }

    private string NormalizeNationality(string requestedNationality, HumanoidCharacterProfile profile)
    {
        if (string.IsNullOrWhiteSpace(requestedNationality))
            return string.Empty;

        if (_prototypeManager.TryIndex<NationalityPrototype>(requestedNationality, out var nationality)
            && RequirementsMet(nationality.Requirements, profile.WithNationality(requestedNationality)))
        {
            return requestedNationality;
        }

        if (_prototypeManager.TryIndex<NationalityPrototype>(SharedHumanoidAppearanceSystem.DefaultNationality, out var defaultNationality)
            && RequirementsMet(defaultNationality.Requirements, profile.WithNationality(defaultNationality.ID)))
        {
            return defaultNationality.ID;
        }

        foreach (var prototype in _prototypeManager.EnumeratePrototypes<NationalityPrototype>())
        {
            if (RequirementsMet(prototype.Requirements, profile.WithNationality(prototype.ID)))
                return prototype.ID;
        }

        return SharedHumanoidAppearanceSystem.DefaultNationality;
    }

    private string NormalizeEmployer(string requestedEmployer, HumanoidCharacterProfile profile)
    {
        if (string.IsNullOrWhiteSpace(requestedEmployer))
            return string.Empty;

        if (_prototypeManager.TryIndex<EmployerPrototype>(requestedEmployer, out var employer)
            && RequirementsMet(employer.Requirements, profile.WithEmployer(requestedEmployer)))
        {
            return requestedEmployer;
        }

        if (_prototypeManager.TryIndex<EmployerPrototype>(SharedHumanoidAppearanceSystem.DefaultEmployer, out var defaultEmployer)
            && RequirementsMet(defaultEmployer.Requirements, profile.WithEmployer(defaultEmployer.ID)))
        {
            return defaultEmployer.ID;
        }

        foreach (var prototype in _prototypeManager.EnumeratePrototypes<EmployerPrototype>())
        {
            if (RequirementsMet(prototype.Requirements, profile.WithEmployer(prototype.ID)))
                return prototype.ID;
        }

        return SharedHumanoidAppearanceSystem.DefaultEmployer;
    }

    private bool RequirementsMet(IReadOnlyCollection<JobRequirement>? requirements, HumanoidCharacterProfile profile)
    {
        if (requirements == null || requirements.Count == 0)
            return true;

        foreach (var requirement in requirements)
        {
            if (!requirement.Check(EntityManager, _prototypeManager, profile, EmptyPlayTimes, out _))
                return false;
        }

        return true;
    }
    #endregion

    private void UpdateUserInterface(Entity<GeneralStationRecordConsoleComponent> ent)
    {
        var (uid, console) = ent;
        var owningStation = _station.GetOwningStation(uid);

        if (!TryComp<StationRecordsComponent>(owningStation, out var stationRecords))
        {
            _ui.SetUiState(uid, GeneralStationRecordConsoleKey.Key,
                new GeneralStationRecordConsoleState(null, null, null, console.Filter, ent.Comp.CanDeleteEntries, ent.Comp.MaxStringLength));
            return;
        }

        var listing = _stationRecords.BuildListing((owningStation.Value, stationRecords), console.Filter);

        switch (listing.Count)
        {
            case 0:
                _ui.SetUiState(uid, GeneralStationRecordConsoleKey.Key,
                    new GeneralStationRecordConsoleState(null, null, null, console.Filter, ent.Comp.CanDeleteEntries, ent.Comp.MaxStringLength));
                return;
            default:
                if (console.ActiveKey == null)
                    console.ActiveKey = listing.Keys.First();
                break;
        }

        if (console.ActiveKey is not { } id)
            return;

        var key = new StationRecordKey(id, owningStation.Value);
        _stationRecords.TryGetRecord<GeneralStationRecord>(key, out var record, stationRecords);

        if (record == null)
            console.ActiveKey = null;

        var newState = new GeneralStationRecordConsoleState(console.ActiveKey, record, listing, console.Filter,
            ent.Comp.CanDeleteEntries, ent.Comp.MaxStringLength);
        _ui.SetUiState(uid, GeneralStationRecordConsoleKey.Key, newState);
    }
}
