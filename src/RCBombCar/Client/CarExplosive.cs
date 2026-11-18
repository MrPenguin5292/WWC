using System;
using Comfort.Common;
using EFT;
using EFT.InventoryLogic;
using UnityEngine;

namespace RCBombCar.Client;

/// <summary>The car's charge as the game's explosion code sees it. Numbers come from config.json; fragments are borrowed from a real
/// F-1 grenade item (its fragment ammo), so the shrapnel behaves like the game's own.</summary>
internal sealed class CarExplosive : IExplosiveItem
{
    const string FragmentDonor = "5710c24ad2720bc3458b45a3";   // F-1 hand grenade
    readonly BlastSettings blast;
    readonly ThrowWeap? donor;

    public CarExplosive(BlastSettings blast)
    {
        this.blast = blast;
        try { donor = Singleton<ItemFactory>.Instance.CreateItem(new MongoID(true).ToString(), FragmentDonor, null) as ThrowWeap; }
        catch (Exception e) { Plugin.ErrorOnce("fragment donor", e); }
    }

    public Vector3 Blindness => Vector3.zero;
    // the same shape as the grenades' numbers: x/y are distances, z the strength or length of the effect
    public Vector3 Contusion => new(2f, blast.maxRadius * 0.8f, 20f);
    public Vector3 ArmorDistanceDistanceDamage => new(1f, blast.killRadius, 40f);
    public float MinExplosionDistance => blast.killRadius;
    public float MaxExplosionDistance => blast.maxRadius;
    public int FragmentsCount => donor != null ? blast.fragments : 0;
    public float GetStrength => blast.strength;
    public bool IsDummy => false;
    public Ammo CreateFragment() => donor!.CreateFragment();
}
