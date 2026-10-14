using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace UnityEngine
{
    public class Object
    {
        private static int sequence;
        private readonly int id;
        public bool Destroyed;
        public Object(int id = 0) { this.id = id == 0 ? ++sequence : id; }
        public int GetInstanceID() => id;
        public static implicit operator bool(Object? value) => !ReferenceEquals(value, null) && !value.Destroyed;
    }
    public sealed class GameObject : Object
    {
        private readonly Dictionary<Type, Component> components = new();
        public readonly Transform transform;
        public Scene scene;
        public int ComponentReads;
        public GameObject(int scene = 1, int id = 0) : base(id) { this.scene = new Scene { handle = scene, isLoaded = true }; transform = new Transform(this); }
        public void Attach(Component component) { components[component.GetType()] = component; component.gameObject = this; }
        public T? GetComponent<T>() where T : Component { ComponentReads++; foreach (var value in components.Values) if (value is T match) return match; return null; }
    }
    public struct Scene { public int handle; public bool isLoaded; }
    public struct Vector3 { }
    public class Component : Object
    {
        public GameObject gameObject = null!;
        public Transform transform => gameObject.transform;
        public Component() { new GameObject().Attach(this); }
        public T? GetComponent<T>() where T : Component => gameObject.GetComponent<T>();
    }
    public class Transform : Object { public GameObject gameObject; public Transform? parent; public Transform(GameObject source) { gameObject = source; } }
}
namespace Photon.Pun { public class PhotonView : UnityEngine.Component { } }
public class Character : UnityEngine.Component { public bool IsPlayerControlled = true; }
public enum ItemState { Ground, Held, InBackpack }
public struct Maybe<T> { public bool IsSome; public T Value; public static Maybe<T> Some(T value) => new() { IsSome = true, Value = value }; }
public struct BackpackReference
{
    public enum BackpackType { Item, Equipped }
    public BackpackType type;
    public Photon.Pun.PhotonView? view;
}
public class Item : UnityEngine.Component
{
    public ItemState itemState;
    public Character? trueHolderCharacter;
    public Maybe<(byte, BackpackReference)> backpackReference;
    public int NativeCalls;
    [MethodImpl(MethodImplOptions.NoInlining)] internal void SetState(ItemState state, Character? character = null) { NativeCalls++; itemState = state; trueHolderCharacter = state == ItemState.Held ? character : null; }
    [MethodImpl(MethodImplOptions.NoInlining)] public void PutInBackpackRPC(byte slot, BackpackReference reference) { NativeCalls++; backpackReference = Maybe<(byte, BackpackReference)>.Some((slot, reference)); SetState(ItemState.InBackpack); }
    [MethodImpl(MethodImplOptions.NoInlining)] private void OnDestroy() { NativeCalls++; }
    public void DestroyNative(bool fakeNull = false) { if (fakeNull) { Destroyed = true; gameObject.Destroyed = true; } OnDestroy(); }
}
public class StickyItemComponent : UnityEngine.Component
{
    public Item item = null!;
    public Character? stuckToCharacter;
    [MethodImpl(MethodImplOptions.NoInlining)] internal virtual void StickToCharacterLocal(Character character, Bodypart bodypart, UnityEngine.Vector3 worldOffset) { stuckToCharacter = character; }
}
public enum Bodypart { Head }
public interface ISpawner { List<Photon.Pun.PhotonView> TrySpawnItems(); }
public class Spawner : UnityEngine.Component, ISpawner
{
    public List<Photon.Pun.PhotonView> Results = new();
    public int Calls;
    public Action? BeforeReturn;
    [MethodImpl(MethodImplOptions.NoInlining)] public List<Photon.Pun.PhotonView> TrySpawnItems() { Calls++; return SpawnItems(new()); }
    [MethodImpl(MethodImplOptions.NoInlining)] public virtual List<Photon.Pun.PhotonView> SpawnItems(List<UnityEngine.Transform> spawnSpots) { Calls++; BeforeReturn?.Invoke(); return Results; }
}
public class BerryBush : Spawner
{
    [MethodImpl(MethodImplOptions.NoInlining)] public override List<Photon.Pun.PhotonView> SpawnItems(List<UnityEngine.Transform> spawnSpots) { Calls++; BeforeReturn?.Invoke(); return Results; }
}
public class SingleItemSpawner : UnityEngine.Component, ISpawner
{
    public List<Photon.Pun.PhotonView> Results = new();
    [MethodImpl(MethodImplOptions.NoInlining)] public virtual List<Photon.Pun.PhotonView> TrySpawnItems() => Results;
}
public enum Segment { Beach, Tropics, Alpine, Caldera, Peak, End }
namespace Peak
{
    public class SpawnedItemTracker : UnityEngine.Component
    {
        public List<Photon.Pun.PhotonView> Results = new();
        [MethodImpl(MethodImplOptions.NoInlining)] public List<Photon.Pun.PhotonView> SpawnAndTrackFromItemHistory() { TrackSpawnedItems(Results); return Results; }
        [MethodImpl(MethodImplOptions.NoInlining)] public void TrackSpawnedItems(List<Photon.Pun.PhotonView> spawnedItems) { }
        public struct SpawnRecord
        {
            public Photon.Pun.PhotonView Result;
            [MethodImpl(MethodImplOptions.NoInlining)] public Photon.Pun.PhotonView Spawn(UnityEngine.Vector3 spawnOffset) => Result;
        }
    }
}
public static class MapHandler
{
    public static UnityEngine.GameObject[] Camps = { new(), new(), new(), new() };
    public static Exception? Failure;
    public static Action? DuringRestore;
    public static UnityEngine.GameObject GetCampfireRoot(int segmentIndex) => Camps[segmentIndex];
    [MethodImpl(MethodImplOptions.NoInlining)] public static void RespawnSavedBaseCampItems(Segment segment, List<int> openLuggageIds, List<Peak.SpawnedItemTracker.SpawnRecord> looseItems)
    { foreach (var item in looseItems) item.Spawn(default); DuringRestore?.Invoke(); if (Failure != null) throw Failure; }
}
namespace PeakReplayLab { internal static class ReplaySafety { public static bool Active; } }
