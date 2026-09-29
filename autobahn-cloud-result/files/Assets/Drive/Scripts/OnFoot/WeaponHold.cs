using UnityEngine;

namespace Autobahn
{
    // Where a held gun is drawn. Runs after the body is animated (and bent by HumanRig's own
    // LateUpdate poses) and after the camera is placed, so the gun never trails the hand or
    // the view by a frame.
    // - Third person: at the right hand (HumanRig.RightHand), the barrel turned onto what the
    //   crosshair is on (the local player) or along the aim (a friend online).
    // - First person (the local player): a view model, the gun and forearms in a fixed pose by
    //   the camera. It is drawn shrunk towards the eye, which looks the same on screen but keeps
    //   it out of walls in front; closer still, the gun is pulled back and down.
    // Also the visual recoil: the gun kicks back and up on a spring, the camera jolts with it.
    [DefaultExecutionOrder(100)]
    public sealed class WeaponHold : MonoBehaviour
    {
        public HumanRig Rig;
        // Set every frame by the owner.
        public Transform Gun;                   // from Weapons.Model, parented to the owner
        public Vector3 Muzzle;                  // in the gun's own frame
        public WeaponDef Def;
        public Quaternion Aim = Quaternion.identity;
        public bool ViewModel;                  // first person: in front of the camera
        public bool AimAtCrosshair;             // third person, local: onto what the camera looks at
        public bool Aiming, Reloading;
        public Transform Ignore;                // not a wall for the view model (the boat we stand in)

        // Where shots leave from (flash, tracer). In first person the full-size point, so the
        // flash sits on the shrunk muzzle on screen.
        public Vector3 MuzzlePoint { get; private set; }
        // The camera's jolt from the last shots, laid on the view by FootManager.
        public Quaternion CameraKick => Quaternion.Euler(-camKick.X, 0, camRoll.X);

        // The view model is this much nearer the eye than it looks.
        const float Shrink = .3f;
        // Walls for the view model and the crosshair point: everything but ourselves (2),
        // loose props (11) and trees (12), as for shots.
        const int ShotMask = ~(1 << 2 | 1 << 11 | 1 << 12);

        struct Spring
        {
            public float X, V;
            public void Step(float dt, float k, float d) { V += (-k * X - d * V) * dt; X += V * dt; }
        }

        Spring back, lift, side, camKick, camRoll;
        float wall;
        Transform rightArm, leftArm;
        static Material sleeveMat, skinMat;
        static readonly RaycastHit[] wallHits = new RaycastHit[8];

        // A shot: the gun kicks, harder for the heavy guns and in first person.
        public void Kick(WeaponDef def, bool firstPerson)
        {
            if (def == null || def.Melee || def.Thrown) return;
            float p = Mathf.Clamp(.35f + def.Kick * .25f, .3f, 2.2f);
            back.V -= (firstPerson ? 1.2f : .7f) * p;
            lift.V += (firstPerson ? 90 : 55) * p;
            side.V += Random.Range(-1f, 1f) * 20 * p;
            camKick.V += (firstPerson ? 28 : 12) * p;
            camRoll.V += Random.Range(-1f, 1f) * (firstPerson ? 8 : 3) * p;
        }

        void Update()
        {
            float dt = Mathf.Min(Time.deltaTime, .05f);
            if (dt <= 0) return;
            back.Step(dt, 220, 22);
            lift.Step(dt, 200, 20);
            side.Step(dt, 200, 24);
            camKick.Step(dt, 160, 18);
            camRoll.Step(dt, 160, 20);
        }

        void LateUpdate()
        {
            bool shown = Gun && Gun.gameObject.activeInHierarchy && Def != null;
            var cam = Camera.main;
            bool view = shown && ViewModel && cam;
            if (shown)
            {
                if (view) PlaceViewModel(cam.transform);
                else PlaceInHand(cam);
            }
            Arms(view);
        }

        void PlaceInHand(Camera cam)
        {
            Vector3 hand = Rig && Rig.RightHand ? Rig.RightHand.position : transform.position + Vector3.up * 1.3f;
            Vector3 grip = hand + Aim * new Vector3(-.02f, .03f, .05f);
            Quaternion r = Aim;
            // The camera looks over the shoulder: turn the barrel onto what the crosshair is on,
            // not parallel to it (a little at most, so nothing close by swings the gun round).
            if (AimAtCrosshair && cam)
            {
                var c = cam.transform;
                Vector3 from = c.position, dir = c.forward;
                float skip = Vector3.Dot(transform.position + Vector3.up * 1.5f - from, dir);
                if (skip > 0) from += dir * skip;
                float range = Mathf.Max(5, Def.Range);
                Vector3 target = Physics.Raycast(from, dir, out RaycastHit hit, range, ShotMask, QueryTriggerInteraction.Ignore) ? hit.point : from + dir * range;
                Vector3 to = target - grip;
                if (to.sqrMagnitude > 4) r = Quaternion.RotateTowards(Aim, Quaternion.LookRotation(to, Aim * Vector3.up), 20);
            }
            if (Reloading) r *= Quaternion.Euler(30, 0, 0);
            grip += r * new Vector3(0, 0, back.X * .5f);
            r *= Quaternion.Euler(-lift.X * .6f, side.X * .5f, 0);
            Gun.localScale = Vector3.one;
            Gun.SetPositionAndRotation(grip, r);
            MuzzlePoint = Gun.TransformPoint(Muzzle);
        }

        void PlaceViewModel(Transform cam)
        {
            bool rifle = Def.Length > .5f;
            Vector3 hold = Aiming ? new Vector3(0, -.13f, rifle ? .22f : .38f) : rifle ? new Vector3(.24f, -.26f, .28f) : new Vector3(.2f, -.2f, .42f);
            Quaternion turn = Quaternion.identity;
            if (Reloading) { hold += new Vector3(0, -.12f, -.05f); turn = Quaternion.Euler(25, -20, 0); }

            // Something right in front of the eye: bring the gun back and down, barrel aside.
            Vector3 eye = cam.position;
            // One look ahead as far as the full-size gun would reach (for the muzzle below);
            // the shrunk gun itself only needs `reach`.
            float full = hold.z + Def.Length * .8f + .1f;
            float reach = full * Shrink + .06f;
            float blocked = WallAhead(eye, cam.forward, Mathf.Max(full, reach));
            float want = blocked < reach ? Mathf.Clamp01((reach - blocked) / reach * 1.8f) : 0;
            wall = Mathf.MoveTowards(wall, want, Time.deltaTime * 6);
            hold += new Vector3(-.05f, -.08f, -.16f) * wall;
            turn = Quaternion.Euler(40 * wall, -15 * wall, 12 * wall) * turn;

            hold.z += back.X;
            Quaternion r = cam.rotation * Quaternion.Euler(-lift.X, side.X, 0) * turn;
            Vector3 at = cam.TransformPoint(hold);
            Gun.SetPositionAndRotation(eye + (at - eye) * Shrink, r);
            Gun.localScale = Vector3.one * Shrink;

            // Full size, the muzzle would be here; never past the wall in front.
            Vector3 m = at + r * Muzzle;
            float along = Vector3.Dot(m - eye, cam.forward);
            float room = Mathf.Max(.02f, blocked - .05f);
            if (blocked < float.MaxValue && along > room) m = eye + (m - eye) * (room / along);
            MuzzlePoint = m;
        }

        // How far in front of the eye the nearest wall is (MaxValue: none within `reach`).
        float WallAhead(Vector3 eye, Vector3 dir, float reach)
        {
            int n = Physics.SphereCastNonAlloc(eye, .05f, dir, wallHits, reach, ShotMask, QueryTriggerInteraction.Ignore);
            float nearest = float.MaxValue;
            for (int i = 0; i < n; i++)
            {
                var h = wallHits[i];
                if (h.distance <= 0) continue;        // started inside: nothing to go by
                if (Ignore && h.collider.transform.IsChildOf(Ignore)) continue;
                if (h.distance < nearest) nearest = h.distance;
            }
            return nearest;
        }

        // The forearms holding the view model: the right one on the grip, the left one under the
        // barrel of a long gun. Plain shapes, placed full size and shrunk like the gun.
        void Arms(bool view)
        {
            bool right = view && !Def.Thrown;
            bool left = right && Def.Length > .5f && !Reloading;
            if (right && !rightArm) rightArm = MakeArm("View arm R");
            if (left && !leftArm) leftArm = MakeArm("View arm L");
            if (rightArm && rightArm.gameObject.activeSelf != right) rightArm.gameObject.SetActive(right);
            if (leftArm && leftArm.gameObject.activeSelf != left) leftArm.gameObject.SetActive(left);
            if (!right) return;
            var cam = Camera.main.transform;
            Vector3 eye = cam.position;
            // The gun is shrunk: back to full size to work out where the hands go.
            Vector3 Full(Vector3 shrunk) => eye + (shrunk - eye) / Shrink;
            Vector3 gripAt = Full(Gun.TransformPoint(new Vector3(0, -.05f, -.01f)));
            PlaceArm(rightArm, eye, gripAt, cam.TransformPoint(new Vector3(.34f, -.5f, .02f)), cam.up);
            if (left)
            {
                Vector3 underBarrel = Full(Gun.TransformPoint(new Vector3(0, -.04f, Def.Length * .45f)));
                PlaceArm(leftArm, eye, underBarrel, cam.TransformPoint(new Vector3(-.26f, -.5f, .1f)), cam.up);
            }
        }

        static void PlaceArm(Transform arm, Vector3 eye, Vector3 hand, Vector3 elbow, Vector3 up)
        {
            Vector3 d = elbow - hand;
            float len = Mathf.Max(.1f, d.magnitude);
            arm.SetPositionAndRotation(eye + (hand - eye) * Shrink, Quaternion.LookRotation(d, up));
            arm.localScale = Vector3.one * Shrink;
            var sleeve = arm.GetChild(0);
            sleeve.localPosition = new Vector3(0, 0, len * .5f + .04f);
            sleeve.localScale = new Vector3(.085f, len * .5f, .085f);
        }

        Transform MakeArm(string name)
        {
            if (!sleeveMat)
            {
                sleeveMat = Art.Material("View sleeve", new Color(.17f, .19f, .21f), 0, .2f);
                skinMat = Art.Material("View hand", new Color(.78f, .58f, .46f), 0, .35f);
            }
            var arm = new GameObject(name).transform;
            arm.SetParent(transform, false);
            // A capsule lies along y: turned to lie along the arm (z).
            var sleeve = Art.Primitive(arm, "Sleeve", Vector3.zero, Vector3.one, sleeveMat, PrimitiveType.Capsule);
            sleeve.localRotation = Quaternion.Euler(90, 0, 0);
            Art.Box(arm, "Hand", new Vector3(0, 0, -.01f), new Vector3(.07f, .09f, .11f), skinMat);
            foreach (var r in arm.GetComponentsInChildren<Renderer>(true))
            {
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                r.gameObject.layer = gameObject.layer;
            }
            arm.gameObject.layer = gameObject.layer;
            return arm;
        }
    }
}
