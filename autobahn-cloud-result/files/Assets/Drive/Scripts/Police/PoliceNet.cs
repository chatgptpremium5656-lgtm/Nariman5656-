using System.IO;
using UnityEngine;

namespace Autobahn
{
    // The police online. The host runs them, like the traffic. 'Q' (host, about 7 a second):
    // every wanted player's stars, and where the police cars and officers are and what they do.
    // 'U' (guest to host): a crime of the guest, maybe against one police unit, with its damage;
    // Crime.Cleared when the guest died or was arrested. Both go out with CoopNet.Send, which
    // puts the sender's slot in the second byte.
    public static class PoliceNet
    {
        public static void SendCrime(Crime c, int unit = 255, float damage = 0)
        {
            if (!Net.CoopNet.IsGuest) return;
            int d = Mathf.Clamp(Mathf.RoundToInt(damage * 10), 0, 65535);
            Net.CoopNet.Active.Send(new[] { (byte)'U', (byte)c, (byte)Mathf.Clamp(unit, 0, 255), (byte)(d & 255), (byte)(d >> 8) });
        }

        public static void Receive(byte[] data)
        {
            var net = Net.CoopNet.Active;
            if (net == null || data.Length < 2 || data[1] == net.Slot) return;
            if (data[0] == 'U')
            {
                if (!Net.CoopNet.IsHost || data.Length < 6 || data[2] > (byte)Crime.Cleared) return;
                PoliceForce.Active?.Apply(data[1], (Crime)data[2], data[3], (data[4] | data[5] << 8) / 10f);
            }
            else if (data[0] == 'Q' && Net.CoopNet.IsGuest)
                PoliceForce.Active?.Mirror(data);
        }

        public static byte[] Snapshot(PoliceForce force)
        {
            var ms = new MemoryStream(640);
            var w = new BinaryWriter(ms);
            w.Write((byte)'Q');
            int n = 0;
            for (int s = 0; s < Wanted.MaxSlots; s++) if (Wanted.StarsOf(s) > 0) n++;
            w.Write((byte)n);
            for (int s = 0; s < Wanted.MaxSlots; s++)
            {
                int stars = Wanted.StarsOf(s);
                if (stars == 0) continue;
                w.Write((byte)s);
                w.Write((byte)stars);
                w.Write((byte)(Wanted.SearchingFor(s) ? 1 : 0));
            }
            n = 0;
            foreach (var car in force.Cars) if (car && car.gameObject.activeSelf) n++;
            w.Write((byte)n);
            foreach (var car in force.Cars)
            {
                if (!car || !car.gameObject.activeSelf) continue;
                var p = car.transform.position;
                w.Write((byte)car.Id);
                w.Write((byte)((car.Siren ? 1 : 0) | (car.Wrecked ? 2 : 0)));
                w.Write(p.x); w.Write(p.y); w.Write(p.z);
                w.Write((ushort)Mathf.RoundToInt(Mathf.Repeat(car.transform.eulerAngles.y, 360) / 360 * 65535));
                w.Write((ushort)Mathf.Clamp(Mathf.RoundToInt(car.Speed * 100), 0, 65535));
            }
            n = 0;
            foreach (var o in force.Officers) if (o && o.gameObject.activeSelf) n++;
            w.Write((byte)n);
            foreach (var o in force.Officers)
            {
                if (!o || !o.gameObject.activeSelf) continue;
                var p = o.transform.position;
                w.Write((byte)o.Id);
                w.Write((byte)((o.Dead ? 1 : 0) | (o.Aiming ? 2 : 0)));
                w.Write((byte)(o.TargetSlot < 0 ? 255 : o.TargetSlot));
                w.Write(o.Shots);
                w.Write(o.Hits);
                w.Write(p.x); w.Write(p.y); w.Write(p.z);
                w.Write((ushort)Mathf.RoundToInt(Mathf.Repeat(o.transform.eulerAngles.y, 360) / 360 * 65535));
                w.Write((sbyte)Mathf.Clamp(o.Forward * 16, -127, 127));
            }
            return ms.ToArray();
        }
    }
}
