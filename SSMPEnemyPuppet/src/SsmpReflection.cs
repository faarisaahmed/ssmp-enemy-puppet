using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using SSMP.Api.Client;
using UnityEngine;

namespace SSMPEnemyPuppet;

/// <summary>One entity, flattened to what this addon needs.</summary>
internal readonly struct EntityView {
    public readonly ushort Id;

    /// <summary>The scene object, whichever side of the host/client pair currently exists.</summary>
    public readonly GameObject Object;

    public EntityView(ushort id, GameObject obj) {
        Id = id;
        Object = obj;
    }
}

/// <summary>
/// Typed access to the parts of SSMP declared <c>internal</c>.
/// </summary>
/// <remarks>
/// SSMP's entity system is entirely internal and its addon API exposes nothing about entities, so
/// the id-to-GameObject mapping this addon is built on has to come from reflection. Resolved once
/// and cached; anything that fails to resolve leaves <see cref="Available"/> false so the addon
/// disables itself at startup rather than throwing inside the game loop.
///
/// Deliberately the same approach, and the same member names, as SSMPEnemySync. If SSMP changes
/// shape, both addons should fail the same way at the same time rather than one silently
/// half-working.
/// </remarks>
internal static class SsmpReflection {
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    /// <summary>Whether every member this addon needs resolved.</summary>
    public static bool Available { get; private set; }

    /// <summary>Why it did not, for logging.</summary>
    public static string UnavailableReason { get; private set; } = "not initialised";

    private static Assembly _ssmp;

    private static FieldInfo _cmEntityManager;
    private static FieldInfo _emEntities;
    private static PropertyInfo _emIsSceneHost;
    private static PropertyInfo _emIsSceneHostDetermined;
    private static PropertyInfo _eId;
    private static PropertyInfo _eObject;
    private static PropertyInfo _pairHost;
    private static PropertyInfo _pairClient;

    public static void Initialize() {
        if (Available || UnavailableReason != "not initialised") {
            return;
        }

        try {
            _ssmp = typeof(ClientAddon).Assembly;

            var clientManagerType = Require(_ssmp.GetType("SSMP.Game.Client.ClientManager"), "ClientManager type");
            var entityManagerType = Require(_ssmp.GetType("SSMP.Game.Client.Entity.EntityManager"), "EntityManager type");
            var entityType = Require(_ssmp.GetType("SSMP.Game.Client.Entity.Entity"), "Entity type");

            _cmEntityManager = Require(clientManagerType.GetField("_entityManager", Instance), "ClientManager._entityManager");

            _emEntities = Require(entityManagerType.GetField("_entities", Instance), "EntityManager._entities");
            _emIsSceneHost = Require(entityManagerType.GetProperty("IsSceneHost", Instance), "EntityManager.IsSceneHost");
            _emIsSceneHostDetermined = Require(entityManagerType.GetProperty("IsSceneHostDetermined", Instance), "EntityManager.IsSceneHostDetermined");

            _eId = Require(entityType.GetProperty("Id", Instance), "Entity.Id");
            _eObject = Require(entityType.GetProperty("Object", Instance), "Entity.Object");

            var pairType = Require(_eObject.PropertyType, "HostClientPair<GameObject> type");
            _pairHost = Require(pairType.GetProperty("Host", Instance), "HostClientPair.Host");
            _pairClient = Require(pairType.GetProperty("Client", Instance), "HostClientPair.Client");

            Available = true;
            UnavailableReason = null;
        } catch (Exception e) {
            Available = false;
            UnavailableReason = e.Message;
        }
    }

    private static T Require<T>(T member, string description) where T : class {
        if (member == null) {
            throw new MissingMemberException($"could not resolve {description}");
        }

        return member;
    }

    public static object GetEntityManager(IClientManager clientManager) => _cmEntityManager.GetValue(clientManager);

    public static bool IsSceneHost(object entityManager) => (bool) _emIsSceneHost.GetValue(entityManager);

    public static bool IsSceneHostDetermined(object entityManager) =>
        (bool) _emIsSceneHostDetermined.GetValue(entityManager);

    /// <summary>
    /// Snapshot the entity table.
    /// </summary>
    /// <remarks>
    /// A copy rather than a live view: the dictionary is mutated by scene loads and network
    /// traffic, so iterating it directly can throw mid-frame.
    /// </remarks>
    public static List<EntityView> SnapshotEntities(object entityManager) {
        var views = new List<EntityView>();
        if (!Available || entityManager == null) {
            return views;
        }

        if (_emEntities.GetValue(entityManager) is not IDictionary entities) {
            return views;
        }

        foreach (DictionaryEntry entry in entities) {
            var entity = entry.Value;
            if (entity == null) {
                continue;
            }

            try {
                var id = (ushort) _eId.GetValue(entity);
                var pair = _eObject.GetValue(entity);

                // Only one side of the pair exists at a time, depending on whether this client
                // is the scene host for that entity.
                var obj = _pairHost.GetValue(pair) as GameObject ?? _pairClient.GetValue(pair) as GameObject;

                if (obj != null) {
                    views.Add(new EntityView(id, obj));
                }
            } catch (Exception) {
                // A single malformed entity should not cost the whole snapshot.
            }
        }

        return views;
    }

    /// <summary>The scene object for one entity id, or null.</summary>
    public static GameObject FindEntityObject(object entityManager, ushort id) {
        foreach (var view in SnapshotEntities(entityManager)) {
            if (view.Id == id) {
                return view.Object;
            }
        }

        return null;
    }

    /// <summary>The entity id for a scene object, or null if it is not a synced entity.</summary>
    /// <remarks>
    /// Needed because the Enemy Behavior API identifies enemies by Unity instance id, which is
    /// local to one machine and meaningless to the other player. Entity ids are the only names
    /// for an enemy that both ends agree on.
    /// </remarks>
    public static ushort? FindEntityId(object entityManager, GameObject obj) {
        if (obj == null) {
            return null;
        }

        foreach (var view in SnapshotEntities(entityManager)) {
            if (ReferenceEquals(view.Object, obj)) {
                return view.Id;
            }
        }

        return null;
    }
}
