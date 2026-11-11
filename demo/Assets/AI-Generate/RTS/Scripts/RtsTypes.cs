using System.Collections.Generic;

namespace IronFront
{
    public enum Team { Neutral = 0, Blue = 1, Red = 2 }
    public enum UnitKind { Engineer = 0, Rifleman = 1, Tank = 2 }
    public enum BuildingKind { HQ = 0, Barracks = 1, Turret = 2 }
    public enum UnitState { Idle = 0, Move = 1, Attack = 2, ToNode = 3, Harvesting = 4, ToBase = 5, Build = 6, Dead = 7 }
    public enum GameState { Running = 0, Paused = 1, Won = 2, Lost = 3 }

    public class UnitDef
    {
        public string Name;
        public string Label;
        public int Cost;
        public int Supply;
        public float Hp;
        public float Damage;
        public float Range;
        public float Cooldown;
        public float Speed;
        public float BuildTime;
        public BuildingKind Producer;
        public float Radius;
        public float Height;
        public float Sight;
    }

    public class BuildingDef
    {
        public string Name;
        public string Label;
        public int Cost;
        public float BuildTime;
        public float Hp;
        public float Sight;
        public int Supply;
        public float Radius;
        public float Height;
        public float Damage;
        public float Range;
        public float Cooldown;
    }

    /// <summary>数值唯一真源：HUD、AI、战斗全部读这里，禁止各处硬编码。</summary>
    public static class RtsDefs
    {
        private static Dictionary<UnitKind, UnitDef> _units;
        private static Dictionary<BuildingKind, BuildingDef> _buildings;

        public static Dictionary<UnitKind, UnitDef> Units
        {
            get { Ensure(); return _units; }
        }

        public static Dictionary<BuildingKind, BuildingDef> Buildings
        {
            get { Ensure(); return _buildings; }
        }

        public static UnitDef U(UnitKind k)
        {
            Ensure();
            return _units[k];
        }

        public static BuildingDef B(BuildingKind k)
        {
            Ensure();
            return _buildings[k];
        }

        private static void Ensure()
        {
            if (_units != null) return;

            _units = new Dictionary<UnitKind, UnitDef>();

            _units[UnitKind.Engineer] = new UnitDef
            {
                Name = "ENGINEER", Label = "Engineer",
                Cost = 60, Supply = 1, Hp = 70f,
                Damage = 3f, Range = 2f, Cooldown = 1.2f, Speed = 3.6f,
                BuildTime = 6f, Producer = BuildingKind.HQ,
                Radius = 0.36f, Height = 1.25f, Sight = 8f
            };

            _units[UnitKind.Rifleman] = new UnitDef
            {
                Name = "RIFLEMAN", Label = "Rifleman",
                Cost = 90, Supply = 1, Hp = 110f,
                Damage = 9f, Range = 6.5f, Cooldown = 0.8f, Speed = 3.4f,
                BuildTime = 8f, Producer = BuildingKind.Barracks,
                Radius = 0.36f, Height = 1.3f, Sight = 9f
            };

            _units[UnitKind.Tank] = new UnitDef
            {
                Name = "TANK", Label = "Tank",
                Cost = 320, Supply = 3, Hp = 380f,
                Damage = 28f, Range = 8.5f, Cooldown = 1.6f, Speed = 2.4f,
                BuildTime = 14f, Producer = BuildingKind.Barracks,
                Radius = 0.95f, Height = 1.6f, Sight = 11f
            };

            _buildings = new Dictionary<BuildingKind, BuildingDef>();

            _buildings[BuildingKind.HQ] = new BuildingDef
            {
                Name = "HQ", Label = "Headquarters",
                Cost = 0, BuildTime = 0f, Hp = 3000f, Sight = 18f, Supply = 20,
                Radius = 4.2f, Height = 5.2f,
                Damage = 0f, Range = 0f, Cooldown = 0f
            };

            _buildings[BuildingKind.Barracks] = new BuildingDef
            {
                Name = "BARRACKS", Label = "Barracks",
                Cost = 350, BuildTime = 12f, Hp = 1200f, Sight = 14f, Supply = 5,
                Radius = 3f, Height = 4f,
                Damage = 0f, Range = 0f, Cooldown = 0f
            };

            _buildings[BuildingKind.Turret] = new BuildingDef
            {
                Name = "TURRET", Label = "Turret",
                Cost = 220, BuildTime = 8f, Hp = 900f, Sight = 15f, Supply = 0,
                Radius = 1.7f, Height = 3.6f,
                Damage = 20f, Range = 11f, Cooldown = 1.2f
            };
        }

        public static UnityEngine.Color TeamColor(Team t)
        {
            if (t == Team.Blue) return new UnityEngine.Color(0.184f, 0.435f, 0.816f);
            if (t == Team.Red) return new UnityEngine.Color(0.753f, 0.224f, 0.169f);
            return new UnityEngine.Color(0.541f, 0.561f, 0.596f);
        }
    }
}
