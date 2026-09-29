using System;
using System.Collections;
using UnityEngine;

namespace Autobahn
{
    // Automated check (DriveValidation): a pistol shot at a traffic car makes the player wanted,
    // a police car comes to them, and out of police sight the stars go away again (the calm-down
    // is shortened to a few seconds here). The police are switched off again afterwards.
    public static class PoliceQA
    {
        public static IEnumerator Run(RunSession run, Action<string, bool, string> check)
        {
            var force = PoliceForce.Active;
            if (!force)
            {
                check("Police: wanted level", false, "no police force in the scene");
                yield break;
            }
            var car = run.Player;
            var traffic = run.Traffic;
            bool trafficWasOn = traffic.Simulate;
            traffic.Simulate = false;
            Wanted.TestMode = true;
            Wanted.ClearAll();
            force.Suspended = false;
            FootManager.Automated = true;
            car.Input.SetTest(0, 0, 0);
            car.Teleport(1200, 1);
            yield return new WaitForSeconds(1f);
            FootManager.Instance.GetOut();
            yield return new WaitForSeconds(1f);
            var foot = FootPlayer.Active;
            // A traffic car stands in the left lane ahead; the view looks straight at it.
            var target = traffic.Cars[5];
            target.OnGrid = false;
            target.gameObject.SetActive(true);
            target.Place(1216, 0, 0);
            yield return new WaitForSeconds(.3f);
            FootManager.FreeCamera = true;
            var eye = Camera.main.transform;
            if (foot)
            {
                foot.Select(1);
                eye.position = foot.transform.position + Vector3.up * 1.6f;
                eye.LookAt(target.transform.position + Vector3.up * .8f);
                yield return new WaitForSeconds(.4f);
                eye.position = foot.transform.position + Vector3.up * 1.6f;
                eye.LookAt(target.transform.position + Vector3.up * .8f);
                foot.TestFire();
            }
            yield return null;
            int stars = Wanted.Stars;
            check("Police: shot at traffic makes you wanted", foot && stars >= 1, $"{stars} star(s) after one pistol shot at a traffic car");

            // The police come: a car within 60 m of the person.
            float start = Time.time, nearest = float.MaxValue;
            while (Time.time - start < 30 && nearest > 60)
            {
                yield return new WaitForSeconds(.25f);
                if (!foot) break;
                // Seen by the police the whole time: the stars must hold while they come.
                foreach (var police in force.Cars)
                    if (police && police.gameObject.activeSelf && !police.Wrecked)
                        nearest = Mathf.Min(nearest, Vector3.Distance(police.transform.position, foot.transform.position));
            }
            check("Police: a police car arrives", nearest <= 60 && Wanted.Stars >= 1,
                $"{force.Spawned} police car(s) sent, nearest {(nearest < float.MaxValue ? nearest.ToString("0") + " m" : "none")} after {Time.time - start:0.0} s; {Wanted.Stars} star(s)");

            // Out of their sight (all units called off) the stars fade after the calm-down.
            Wanted.TestCalmDelay = 3;
            force.Suspended = true;
            start = Time.time;
            while (Time.time - start < 10 && Wanted.Stars > 0)
                yield return new WaitForSeconds(.25f);
            check("Police: wanted level fades out of sight", Wanted.Stars == 0, $"stars gone after {Time.time - start:0.0} s out of police sight (calm-down shortened to 3 s)");

            Wanted.TestCalmDelay = -1;
            Wanted.ClearAll();
            force.DismissAll();
            force.Suspended = false;
            Wanted.TestMode = false;
            FootManager.FreeCamera = false;
            if (FootPlayer.Active) FootManager.Instance.GetIn();
            FootManager.Automated = false;
            target.gameObject.SetActive(false);
            traffic.Simulate = trafficWasOn;
            yield return new WaitForSeconds(.5f);
        }
    }
}
