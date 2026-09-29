using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;

namespace Autobahn
{
    // A person: the Adventurer Blake model (Asset Store) driven by a playable graph built in code
    // (no animator controller asset): idle / walk / run / strafe / in the air on the whole body,
    // an upper-body layer for holding a gun, firing and punching, and a death clip on top.
    // Used for the local player on foot and for friends on foot online.
    public sealed class HumanRig : MonoBehaviour
    {
        public const string Root = "Characters/Blake/";
        public Animator Anim { get; private set; }
        public Transform Model { get; private set; }
        public Transform RightHand { get; private set; }
        public Transform Head { get; private set; }

        // Inputs, set every frame by the owner.
        public float Forward, Strafe;          // m/s in the body's frame
        public bool Grounded = true, Dead, Armed;
        public Vector3 AimDirection = Vector3.forward;
        public bool HideHead;                  // first person: the head is not drawn
        public bool Kneeling;                  // on both knees, head bowed, hands together: "sorry"
        public bool Hidden;                    // riding in a car: not drawn at all
        public bool Crouching;                 // squatting down (Ctrl): knees bent, lower, can still walk
        float crouch;
        float kneel;
        TextMesh bubble;

        PlayableGraph graph;
        AnimationMixerPlayable loco, upper, top;
        AnimationLayerMixerPlayable layers;
        readonly AnimationClipPlayable[] locoClips = new AnimationClipPlayable[6];
        AnimationClipPlayable aimIdle, aimShoot, punch, dead;
        readonly float[] locoWeight = new float[6], wantWeight = new float[6];
        float upperWeight, shootFor, punchFor, deadWeight;
        Vector3 headScale = Vector3.one;

        static AnimationClip[] clips;
        static AvatarMask upperMask;
        static readonly string[] ClipNames =
        {
            "idle", "walking", "running", "walking left", "walking right", "jump mid air",
            "idle with pistol", "pistol shooting with recoil", "punch left and right", "dead",
        };

        public static bool Available => Resources.Load<GameObject>(Root + "Prefab/The Adventurer Blake") != null;

        public static HumanRig Create(Transform parent, Color tint, bool tinted)
        {
            var go = new GameObject("Person");
            go.transform.SetParent(parent, false);
            var rig = go.AddComponent<HumanRig>();
            rig.Build(tint, tinted);
            return rig;
        }

        static void LoadClips()
        {
            if (clips != null) return;
            clips = new AnimationClip[ClipNames.Length];
            for (int i = 0; i < ClipNames.Length; i++)
            {
                clips[i] = Resources.Load<AnimationClip>(Root + "Animations/" + ClipNames[i]);
                if (!clips[i]) Debug.LogWarning("AUTOBAHN person: missing clip " + ClipNames[i]);
            }
            upperMask = new AvatarMask();
            for (int p = 0; p < (int)AvatarMaskBodyPart.LastBodyPart; p++)
            {
                var part = (AvatarMaskBodyPart)p;
                bool on = part == AvatarMaskBodyPart.Head || part == AvatarMaskBodyPart.LeftArm || part == AvatarMaskBodyPart.RightArm
                       || part == AvatarMaskBodyPart.LeftFingers || part == AvatarMaskBodyPart.RightFingers;
                upperMask.SetHumanoidBodyPartActive(part, on);
            }
        }

        void Build(Color tint, bool tinted)
        {
            LoadClips();
            var source = Resources.Load<GameObject>(Root + "Prefab/The Adventurer Blake");
            if (!source) { Debug.LogWarning("AUTOBAHN person: model missing"); return; }
            Model = Instantiate(source, transform, false).transform;
            Model.name = "Blake";
            foreach (var c in Model.GetComponentsInChildren<Collider>(true)) Destroy(c);
            Anim = Model.GetComponentInChildren<Animator>();
            if (!Anim) Anim = Model.gameObject.AddComponent<Animator>();
            Anim.runtimeAnimatorController = null;
            Anim.applyRootMotion = false;
            Anim.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            foreach (var r in Model.GetComponentsInChildren<Renderer>(true))
            {
                // The pack ships a mock gun in his hand: we hold our own.
                if (r.sharedMaterial && r.sharedMaterial.name.Contains("mock_gun")) { r.enabled = false; continue; }
                if (r is SkinnedMeshRenderer s) s.updateWhenOffscreen = true;
                if (tinted)
                {
                    var mats = r.materials;
                    foreach (var m in mats) { m.color = Color.Lerp(Color.white, tint, .45f); ownMaterials.Add(m); }
                    r.materials = mats;
                }
            }
            if (Anim.isHuman)
            {
                RightHand = Anim.GetBoneTransform(HumanBodyBones.RightHand);
                Head = Anim.GetBoneTransform(HumanBodyBones.Head);
                // Scale the model to a 1.82 m tall man, whatever units the pack used.
                if (Head)
                {
                    float headY = Head.position.y - transform.position.y;
                    if (headY > .2f) Model.localScale *= 1.68f / headY;
                }
            }
            if (Head) headScale = Head.localScale;
            if (!RightHand) RightHand = Model;

            graph = PlayableGraph.Create("Person " + GetInstanceID());
            graph.SetTimeUpdateMode(DirectorUpdateMode.GameTime);
            var output = AnimationPlayableOutput.Create(graph, "Body", Anim);
            loco = AnimationMixerPlayable.Create(graph, 6);
            for (int i = 0; i < 6; i++)
            {
                locoClips[i] = AnimationClipPlayable.Create(graph, clips[i]);
                graph.Connect(locoClips[i], 0, loco, i);
            }
            upper = AnimationMixerPlayable.Create(graph, 3);
            aimIdle = AnimationClipPlayable.Create(graph, clips[6]);
            aimShoot = AnimationClipPlayable.Create(graph, clips[7]);
            punch = AnimationClipPlayable.Create(graph, clips[8]);
            graph.Connect(aimIdle, 0, upper, 0);
            graph.Connect(aimShoot, 0, upper, 1);
            graph.Connect(punch, 0, upper, 2);
            layers = AnimationLayerMixerPlayable.Create(graph, 2);
            graph.Connect(loco, 0, layers, 0);
            graph.Connect(upper, 0, layers, 1);
            layers.SetLayerMaskFromAvatarMask(1, upperMask);
            layers.SetInputWeight(0, 1);
            dead = AnimationClipPlayable.Create(graph, clips[9]);
            top = AnimationMixerPlayable.Create(graph, 2);
            graph.Connect(layers, 0, top, 0);
            graph.Connect(dead, 0, top, 1);
            output.SetSourcePlayable(top);
            locoWeight[0] = 1;
            graph.Play();
        }

        public void Shoot() { shootFor = .28f; if (aimShoot.IsValid()) aimShoot.SetTime(0); }

        public void Punch() { punchFor = .7f; if (punch.IsValid()) punch.SetTime(0); }

        public void Die() { if (!Dead && dead.IsValid()) dead.SetTime(0); Dead = true; }

        public void Revive() { Dead = false; deadWeight = 0; }

        static void Loop(AnimationClipPlayable p)
        {
            if (!p.IsValid()) return;
            var clip = p.GetAnimationClip();
            if (!clip || clip.length <= 0) return;
            double t = p.GetTime();
            if (t > clip.length) p.SetTime(t % clip.length);
            else if (t < 0) p.SetTime(clip.length + t % clip.length);
        }

        void Update()
        {
            if (!graph.IsValid()) return;
            float dt = Time.deltaTime;
            // Whole-body locomotion weights from the speed in the body's frame.
            var want = wantWeight;
            System.Array.Clear(want, 0, 6);
            if (!Grounded) want[5] = 1;
            else
            {
                float f = Mathf.Abs(Forward), s = Mathf.Abs(Strafe);
                float moving = Mathf.Clamp01(Mathf.Max(f, s) / 1.6f);
                float run = Mathf.Clamp01((f - 2.8f) / 2.2f);
                want[0] = 1 - moving;
                float side = s / Mathf.Max(.01f, f + s);
                want[1] = moving * (1 - side) * (1 - run);
                want[2] = moving * (1 - side) * run;
                want[Strafe < 0 ? 3 : 4] = moving * side;
            }
            float sum = 0;
            for (int i = 0; i < 6; i++)
            {
                locoWeight[i] = Mathf.MoveTowards(locoWeight[i], want[i], dt * 6);
                sum += locoWeight[i];
            }
            for (int i = 0; i < 6; i++)
            {
                loco.SetInputWeight(i, sum > 0 ? locoWeight[i] / sum : (i == 0 ? 1 : 0));
                Loop(locoClips[i]);
            }
            // Walking backwards: the walk played in reverse.
            float dir = Forward < -.2f ? -1 : 1;
            locoClips[1].SetSpeed(dir * Mathf.Clamp(Mathf.Abs(Forward) / 1.5f, .6f, 1.6f));
            locoClips[2].SetSpeed(dir * Mathf.Clamp(Mathf.Abs(Forward) / 5f, .7f, 1.4f));

            // Upper body: aiming stance, recoil or a punch.
            shootFor = Mathf.Max(0, shootFor - dt);
            punchFor = Mathf.Max(0, punchFor - dt);
            float targetUpper = Armed || punchFor > 0 ? 1 : 0;
            upperWeight = Mathf.MoveTowards(upperWeight, targetUpper, dt * 5);
            layers.SetInputWeight(1, upperWeight);
            float wPunch = punchFor > 0 ? 1 : 0, wShoot = punchFor > 0 ? 0 : shootFor > 0 ? 1 : 0;
            upper.SetInputWeight(0, 1 - wPunch - wShoot);
            upper.SetInputWeight(1, wShoot);
            upper.SetInputWeight(2, wPunch);
            Loop(aimIdle);

            // Death on top of everything, held on its last frame.
            deadWeight = Mathf.MoveTowards(deadWeight, Dead ? 1 : 0, dt * 6);
            top.SetInputWeight(0, 1 - deadWeight);
            top.SetInputWeight(1, deadWeight);
            if (dead.IsValid() && dead.GetAnimationClip() && dead.GetTime() > dead.GetAnimationClip().length)
                dead.SetTime(dead.GetAnimationClip().length);
        }

        Renderer[] bodyRenderers;
        bool hidden;

        void LateUpdate()
        {
            if (Model && Model.gameObject.activeSelf == Hidden) Model.gameObject.SetActive(!Hidden);
            CrouchPose();
            KneelPose();
            AimPose();
            // First person: the body is not drawn (the gun is held in front of the camera).
            if (HideHead == hidden) return;
            hidden = HideHead;
            bodyRenderers ??= Model ? Model.GetComponentsInChildren<Renderer>(true) : new Renderer[0];
            foreach (var r in bodyRenderers)
                if (r && !(r.sharedMaterial && r.sharedMaterial.name.Contains("mock_gun")))
                    r.shadowCastingMode = hidden ? UnityEngine.Rendering.ShadowCastingMode.ShadowsOnly : UnityEngine.Rendering.ShadowCastingMode.On;
        }

        // The kneeling pose, laid over the animation bone by bone: each bone is turned so the
        // next one lies in a chosen direction. That works whatever the rig's own bone axes are.
        void KneelPose()
        {
            kneel = Mathf.MoveTowards(kneel, Kneeling && !Dead && !Hidden ? 1 : 0, Time.deltaTime * 3.5f);
            ShowBubble(kneel > .6f && !HideHead);
            if (kneel <= 0 || !Anim || !Anim.isHuman) return;
            float w = Mathf.SmoothStep(0, 1, kneel);
            Vector3 up = transform.up, fwd = transform.forward, right = transform.right;
            Transform B(HumanBodyBones b) => Anim.GetBoneTransform(b);
            var hips = B(HumanBodyBones.Hips);
            if (!hips) return;
            var lUp = B(HumanBodyBones.LeftUpperLeg); var lLow = B(HumanBodyBones.LeftLowerLeg); var lFoot = B(HumanBodyBones.LeftFoot);
            var rUp = B(HumanBodyBones.RightUpperLeg); var rLow = B(HumanBodyBones.RightLowerLeg); var rFoot = B(HumanBodyBones.RightFoot);
            if (!lUp || !lLow || !lFoot || !rUp || !rLow || !rFoot) return;
            float thigh = Vector3.Distance(lUp.position, lLow.position);
            // Hips come down so the thighs stand upright on the knees.
            float hipDrop = hips.position.y - lUp.position.y;
            Vector3 want = new Vector3(hips.position.x, transform.position.y + thigh + .09f + hipDrop, hips.position.z) - fwd * .04f;
            hips.position = Vector3.Lerp(hips.position, want, w);
            foreach (var (upper, lower, foot, side) in new[] { (lUp, lLow, lFoot, -1f), (rUp, rLow, rFoot, 1f) })
            {
                Aim(upper, lower, -up + fwd * .1f + right * side * .08f, w);
                Aim(lower, foot, -fwd - up * .12f, w);
                var toes = B(side < 0 ? HumanBodyBones.LeftToes : HumanBodyBones.RightToes);
                if (toes) Aim(foot, toes, -fwd * .4f - up, w);
            }
            // A bow from the waist and the neck.
            var spine = B(HumanBodyBones.Spine);
            var chest = B(HumanBodyBones.Chest);
            var neck = B(HumanBodyBones.Neck);
            var head = B(HumanBodyBones.Head);
            if (spine) spine.rotation = Quaternion.AngleAxis(14 * w, right) * spine.rotation;
            if (chest) chest.rotation = Quaternion.AngleAxis(8 * w, right) * chest.rotation;
            if (neck) neck.rotation = Quaternion.AngleAxis(22 * w, right) * neck.rotation;
            if (head) head.rotation = Quaternion.AngleAxis(20 * w, right) * head.rotation;
            // Hands pressed together in front of the chest.
            foreach (var (shoulder, elbow, hand, side) in new[]
            {
                (B(HumanBodyBones.LeftUpperArm), B(HumanBodyBones.LeftLowerArm), B(HumanBodyBones.LeftHand), -1f),
                (B(HumanBodyBones.RightUpperArm), B(HumanBodyBones.RightLowerArm), B(HumanBodyBones.RightHand), 1f),
            })
            {
                if (!shoulder || !elbow || !hand) continue;
                Aim(shoulder, elbow, -up * .8f + fwd * .38f + right * side * .06f, w);
                Aim(elbow, hand, up * .45f + fwd * .8f - right * side * .38f, w);
            }
        }

        // Holding a gun: the chest leans with the aim, so the arms (and the gun in the right
        // hand, WeaponHold) go up and down with it. Not in first person: the camera sits on the
        // head, and the body is not drawn there anyway.
        void AimPose()
        {
            float w = Armed && !HideHead && !Hidden ? upperWeight * (1 - deadWeight) * (1 - kneel) : 0;
            if (w <= 0 || !Anim || !Anim.isHuman) return;
            Vector3 aim = AimDirection;
            float pitch = Mathf.Atan2(aim.y, new Vector2(aim.x, aim.z).magnitude) * Mathf.Rad2Deg;
            pitch = Mathf.Clamp(pitch, -55, 55) * w;
            // A turn about the body's right tips it forward: looking up is a negative turn.
            Vector3 right = transform.right;
            var spine = Anim.GetBoneTransform(HumanBodyBones.Spine);
            var chest = Anim.GetBoneTransform(HumanBodyBones.Chest);
            if (spine) spine.rotation = Quaternion.AngleAxis(-pitch * (chest ? .35f : .8f), right) * spine.rotation;
            if (chest) chest.rotation = Quaternion.AngleAxis(-pitch * .45f, right) * chest.rotation;
        }

        // Crouching is laid on top of whatever the legs are doing (added turns, not a fixed pose),
        // so walking while crouched still steps.
        void CrouchPose()
        {
            crouch = Mathf.MoveTowards(crouch, Crouching && !Dead && !Hidden && !Kneeling ? 1 : 0, Time.deltaTime * 5);
            if (crouch <= 0 || !Anim || !Anim.isHuman) return;
            float w = Mathf.SmoothStep(0, 1, crouch);
            Vector3 right = transform.right;
            var hips = Anim.GetBoneTransform(HumanBodyBones.Hips);
            var lUp = Anim.GetBoneTransform(HumanBodyBones.LeftUpperLeg);
            var lLow = Anim.GetBoneTransform(HumanBodyBones.LeftLowerLeg);
            var lFoot = Anim.GetBoneTransform(HumanBodyBones.LeftFoot);
            if (!hips || !lUp || !lLow || !lFoot) return;
            float thigh = Vector3.Distance(lUp.position, lLow.position), shin = Vector3.Distance(lLow.position, lFoot.position);
            const float up = 62, knee = 112;
            float drop = thigh * (1 - Mathf.Cos(up * Mathf.Deg2Rad)) + shin * (1 - Mathf.Cos((knee - up) * Mathf.Deg2Rad));
            hips.position -= transform.up * drop * w;
            foreach (var side in new[] { (HumanBodyBones.LeftUpperLeg, HumanBodyBones.LeftLowerLeg, HumanBodyBones.LeftFoot), (HumanBodyBones.RightUpperLeg, HumanBodyBones.RightLowerLeg, HumanBodyBones.RightFoot) })
            {
                var a = Anim.GetBoneTransform(side.Item1); var b = Anim.GetBoneTransform(side.Item2); var c = Anim.GetBoneTransform(side.Item3);
                if (!a || !b || !c) continue;
                a.rotation = Quaternion.AngleAxis(-up * w, right) * a.rotation;
                b.rotation = Quaternion.AngleAxis(knee * w, right) * b.rotation;
                c.rotation = Quaternion.AngleAxis(-(knee - up) * w, right) * c.rotation;
            }
            var spine = Anim.GetBoneTransform(HumanBodyBones.Spine);
            if (spine) spine.rotation = Quaternion.AngleAxis(18 * w, right) * spine.rotation;
        }

        static void Aim(Transform bone, Transform child, Vector3 dir, float w)
        {
            Vector3 now = child.position - bone.position;
            if (now.sqrMagnitude < 1e-6f || dir.sqrMagnitude < 1e-6f) return;
            var turn = Quaternion.FromToRotation(now, dir.normalized);
            bone.rotation = Quaternion.Slerp(Quaternion.identity, turn, w) * bone.rotation;
        }

        void ShowBubble(bool on)
        {
            if (on && !bubble)
            {
                bubble = Art.Text(transform, "ПРОСТИ, БРАТ!", Vector3.up * 1.62f, .1f, new Color(1f, .92f, .6f));
                bubble.name = "Sorry bubble";
            }
            if (!bubble) return;
            if (bubble.gameObject.activeSelf != on) bubble.gameObject.SetActive(on);
            if (!on) return;
            var cam = Camera.main;
            if (cam) bubble.transform.rotation = Quaternion.LookRotation(bubble.transform.position - cam.transform.position);
            bubble.transform.localPosition = Vector3.up * (1.62f + .04f * Mathf.Sin(Time.time * 3));
        }

        // Tinted copies made for a friend's avatar: freed with it.
        readonly System.Collections.Generic.List<Material> ownMaterials = new();

        void OnDestroy()
        {
            foreach (var m in ownMaterials) if (m) Destroy(m);
            if (graph.IsValid()) graph.Destroy();
        }
    }
}
