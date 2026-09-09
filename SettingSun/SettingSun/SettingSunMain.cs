using dc;
using dc.en;
using HaxeProxy.Runtime;
using ModCore.Events.Interfaces.Game;
using ModCore.Events.Interfaces.Game.Hero;
using ModCore.Mods;
using ModCore.Modules;

namespace SettingSun;

public class SettingSunMain : ModBase, IOnGameExit, IOnHeroUpdate
{
	private double _timer;

	public SettingSunMain(ModInfo info) : base(info) { }

	public override void Initialize()
	{
		base.Initialize();
		System.Console.WriteLine("[SettingSun] Loaded");
	}

	void IOnHeroUpdate.OnHeroUpdate(double dt)
	{
		Hero? hero = Game.Instance.HeroInstance;
		if (hero == null) return;

		if (!hero.hasAnySpeedBuff()) return;

		// Call game's drawTrail with black + Alpha blend
		_timer -= dt;
		if (_timer <= 0)
		{
			_timer = 0.04;
			// col=0 black, add=false → Alpha blend darkens BG
			hero.drawTrail(0x000000, (bool?)false, (double?)0.5);
		}
	}

	void IOnGameExit.OnGameExit() { }
}