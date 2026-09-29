using UnityEngine;

namespace Autobahn.Net
{
    // A friend on foot: the same person model, tinted in the friend's colour, following their
    // position smoothly, with their gun, the flash of their shots, and a body that can be hit.
    public sealed class RemoteAvatar : MonoBehaviour
    {
        public int Slot { get; private set; }
        public bool Dead { get; private set; }
        public bool Visible { get; private set; }
        public bool Riding => RideSlot >= 0;
        public int RideSlot { get; private set; } = -1;
        HumanRig rig;
        WeaponHold hold;
        CapsuleCollider body;
        Vector3 target, velocity;
        float yaw, targetYaw, pitch;
        int weapon = -1, gunFor = -1;
        Transform gun;
        Vector3 muzzle;
        float lastPacket;

        public const int ShootableLayer = 13;

        public static RemoteAvatar Create(int slot, Color colour)
        {
            var go = new GameObject("Friend on foot " + slot);
            var a = go.AddComponent<RemoteAvatar>();
            a.Slot = slot;
            a.rig = HumanRig.Create(go.transform, colour, true);
            a.hold = go.AddComponent<WeaponHold>();
            a.hold.Rig = a.rig;
            a.body = go.AddComponent<CapsuleCollider>();
            a.body.height = 1.8f;
            a.body.radius = .35f;
            a.body.center = new Vector3(0, .9f, 0);
            // Its own layer: bullets hit it, cars drive through it (the person decides for
            // themselves in their own game when a car has knocked them down).
            go.layer = ShootableLayer;
            go.SetActive(false);
            return a;
        }

        // Inside an aircraft: not drawn, not hit, for as long as the aircraft keeps saying so.
        float stowedUntil;
        public void Stow() => stowedUntil = Time.time + .4f;
        public bool Stowed => Time.time < stowedUntil;

        public void Hide()
        {
            if (Visible) gameObject.SetActive(false);
            Visible = false;
        }

        public void Push(byte flags, Vector3 pos, float y, float p, float forward, float strafe, int w, int health, int ride = -1)
        {
            lastPacket = Time.time;
            if (!Visible)
            {
                Visible = true;
                gameObject.SetActive(true);
                transform.position = pos;
                yaw = y;
            }
            velocity = (pos - target) * 12;
            target = pos;
            targetYaw = y;
            pitch = p;
            weapon = w;
            bool dead = (flags & FootNet.FlagDead) != 0;
            if (dead && !Dead) rig.Die();
            if (!dead && Dead) rig.Revive();
            Dead = dead;
            RideSlot = (flags & FootNet.FlagRide) != 0 ? ride : -1;
            body.enabled = !dead && !Riding;
            rig.Hidden = Riding || Stowed;
            rig.Kneeling = (flags & FootNet.FlagKneel) != 0;
            rig.Crouching = (flags & FootNet.FlagCrouch) != 0;
            body.height = rig.Crouching ? 1.25f : 1.8f;
            body.center = new Vector3(0, body.height / 2, 0);
            rig.Forward = forward;
            rig.Strafe = strafe;
            rig.Grounded = (flags & FootNet.FlagGround) != 0;
            rig.Armed = w > 0 && w < Weapons.All.Length && !Weapons.All[w].Thrown && !dead && !rig.Kneeling;
        }

        public void Fired(int w, Vector3 at)
        {
            weapon = w;
            var def = w >= 0 && w < Weapons.All.Length ? Weapons.All[w] : Weapons.All[1];
            if (def.Melee) { rig.Punch(); return; }
            if (def.Thrown)
            {
                // A friend's bomb: `at` is its starting velocity. It hurts only what is ours.
                rig.Punch();
                Vector3 hand = rig.RightHand ? rig.RightHand.position : transform.position + Vector3.up * 1.5f;
                Bomb.Throw(hand, at, null, false);
                return;
            }
            rig.Shoot();
            hold.Kick(def, false);
            Vector3 from = gun ? gun.TransformPoint(muzzle) : transform.position + Vector3.up * 1.4f;
            Weapons.Flash(from, (at - from).normalized);
            Weapons.Tracer(from, at);
            Weapons.Sound(Weapons.Shot, from, .9f, def.Pitch);
        }

        void Update()
        {
            if (Time.time - lastPacket > 3) { Hide(); return; }
            // Smooth follow with a little prediction, so 12 updates a second read as walking.
            Vector3 predicted = target + velocity * Mathf.Min(.1f, Time.time - lastPacket);
            transform.position = Vector3.Lerp(transform.position, predicted, 1 - Mathf.Exp(-Time.deltaTime * 14));
            yaw = Mathf.LerpAngle(yaw, targetYaw, 1 - Mathf.Exp(-Time.deltaTime * 16));
            transform.rotation = Quaternion.Euler(0, yaw, 0);
            rig.AimDirection = Quaternion.Euler(pitch, yaw, 0) * Vector3.forward;
        }

        void LateUpdate()
        {
            bool hidden = Riding || Stowed;
            if (rig.Hidden != hidden) { rig.Hidden = hidden; body.enabled = !Dead && !hidden; }
            if (gunFor != weapon)
            {
                if (gun) Destroy(gun.gameObject);
                gun = weapon > 0 && weapon < Weapons.All.Length ? Weapons.Model(Weapons.All[weapon], transform, out muzzle) : null;
                gunFor = weapon;
            }
            if (gun) gun.gameObject.SetActive(!Dead && !Riding && !rig.Kneeling);
            // In the right hand along their aim, kicking on their shots: placed by WeaponHold
            // after the body is animated.
            hold.Gun = gun;
            hold.Muzzle = muzzle;
            hold.Def = weapon >= 0 && weapon < Weapons.All.Length ? Weapons.All[weapon] : null;
            hold.Aim = Quaternion.Euler(pitch, yaw, 0);
        }
    }
}
