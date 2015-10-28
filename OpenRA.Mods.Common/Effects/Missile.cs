#region Copyright & License Information
/*
 * Copyright 2007-2015 The OpenRA Developers (see AUTHORS)
 * This file is part of OpenRA, which is free software. It is made
 * available to you under the terms of the GNU General Public License
 * as published by the Free Software Foundation. For more information,
 * see COPYING.
 */
#endregion

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using OpenRA.Effects;
using OpenRA.GameRules;
using OpenRA.Graphics;
using OpenRA.Mods.Common;
using OpenRA.Mods.Common.Graphics;
using OpenRA.Mods.Common.Traits;
using OpenRA.Traits;

namespace OpenRA.Mods.Common.Effects
{
	public class MissileInfo : IProjectileInfo
	{
		[Desc("Name of the image containing the projectile sequence.")]
		public readonly string Image = null;

		[Desc("Projectile sequence name.")]
		[SequenceReference("Image")] public readonly string Sequence = "idle";

		[Desc("Palette used to render the projectile sequence.")]
		[PaletteReference] public readonly string Palette = "effect";

		[Desc("Should the projectile's shadow be rendered?")]
		public readonly bool Shadow = false;

		[Desc("Minimum vertical launch angle (pitch).")]
		public readonly WAngle MinimumLaunchAngle = new WAngle(-64);

		[Desc("Maximum vertical launch angle (pitch).")]
		public readonly WAngle MaximumLaunchAngle = new WAngle(128);

		[Desc("Minimum launch speed in WDist / tick")]
		public readonly WDist MinimumLaunchSpeed = new WDist(75);

		[Desc("Maximum launch speed in WDist / tick")]
		public readonly WDist MaximumLaunchSpeed = new WDist(200);

		[Desc("Maximum projectile speed in WDist / tick")]
		public readonly WDist MaximumSpeed = new WDist(384);

		[Desc("Projectile acceleration when propulsion activated.")]
		public readonly WDist Acceleration = new WDist(5);

		[Desc("How many ticks before this missile is armed and can explode.")]
		public readonly int Arm = 0;

		[Desc("Is the missile blocked by actors with BlocksProjectiles: trait.")]
		public readonly bool Blockable = true;

		[Desc("Width of projectile (used for finding blocking actors).")]
		public readonly WDist Width = new WDist(1);

		[Desc("Extra search radius beyond path for blocking actors.")]
		public readonly WDist TargetExtraSearchRadius = new WDist(1536);

		[Desc("Maximum offset at the maximum range")]
		public readonly WDist Inaccuracy = WDist.Zero;

		[Desc("Probability of locking onto and following target.")]
		public readonly int LockOnProbability = 100;

		[Desc("Horizontal rate of turn.")]
		public readonly int HorizontalRateOfTurn = 5;

		[Desc("Vertical rate of turn.")]
		public readonly int VerticalRateOfTurn = 5;

		[Desc("Gravity applied while in free fall.")]
		public readonly int Gravity = 10;

		[Desc("Run out of fuel after being activated this many ticks. Negative one (-1) for unlimited fuel.")]
		public readonly int RangeLimit = 0;

		[Desc("Explode when running out of fuel.")]
		public readonly bool ExplodeWhenEmpty = true;

		[Desc("Altitude above terrain below which to explode. Zero effectively deactivates airburst.")]
		public readonly WDist AirburstAltitude = WDist.Zero;

		[Desc("Cruise altitude. Zero means no cruise altitude used.")]
		public readonly WDist CruiseAltitude = new WDist(512);

		[Desc("Activate homing mechanism after this many ticks.")]
		public readonly int HomingActivationDelay = 0;

		[Desc("Image that contains the trail animation.")]
		public readonly string TrailImage = null;

		[Desc("Smoke sequence name.")]
		[SequenceReference("TrailImage")] public readonly string TrailSequence = "idle";

		[Desc("Palette used to render the smoke sequence.")]
		[PaletteReference("TrailUsePlayerPalette")] public readonly string TrailPalette = "effect";

		[Desc("Use the Player Palette to render the smoke sequence.")]
		public readonly bool TrailUsePlayerPalette = false;

		[Desc("Interval in ticks between spawning smoke animation.")]
		public readonly int TrailInterval = 2;

		[Desc("Should smoke animation be spawned when the propulsion is not activated.")]
		public readonly bool TrailWhenDeactivated = false;

		public readonly int ContrailLength = 0;

		public readonly int ContrailZOffset = 2047;

		public readonly WDist ContrailWidth = new WDist(64);

		public readonly Color ContrailColor = Color.White;

		public readonly bool ContrailUsePlayerColor = false;

		public readonly int ContrailDelay = 1;

		[Desc("Should missile targeting be thrown off by nearby actors with JamsMissiles.")]
		public readonly bool Jammable = true;

		[Desc("Range of facings by which jammed missiles can stray from current path.")]
		public readonly int JammedDiversionRange = 20;

		[Desc("Explodes when leaving the following terrain type, e.g., Water for torpedoes.")]
		public readonly string BoundToTerrainType = "";

		[Desc("Explodes when inside this proximity radius to target.",
			"Note: If this value is lower than the missile speed, this check might",
			"not trigger fast enough, causing the missile to fly past the target.")]
		public readonly WDist CloseEnough = new WDist(298);

		public IEffect Create(ProjectileArgs args) { return new Missile(this, args); }
	}

	// NOTE: All nontrivial computations assume no increase in initialSpeed (zero acceleration).
	//       Similarly, to obtain more predictable results during manoeuvres that involve
	//	     changing the vertical facing, initialSpeed is not changed. This results in the missile
	//       traveling on an approximately circular trajectory (vertical loop) the radius of
	//       which is stored in the loopRadius variable. At any point in time, there are two
	//       vertical loops associated with the missile. One on which the missile would travel
	//       if its vertical facing were decreasing (the lower vertical loop) and the one on
	//       which its vertical facing would be increasing (the upper vertical loop).
	// NOTE: Functions that receive and handle vertical facings declare those facings as sbytes
	//       if they perform comparisons (explicitly through <, >, != etc. or implicitly through
	//       Clamp, Max, Min, etc.) of the values of those facings. Otherwise, they declare facings
	//       as integers.
	// NOTE: Heights refer to the Z positions in world coordinates.
	//       Altitudes refers to the difference in height between a point and the terrain.
	//       Elevations refer to the difference in height between a point and the missile.
	// NOTE: Speeds and vertical facings that are within prescribed limits are called attainable.
	// NOTE: The higher the speed and the lower the VROT, the greater the needed lookahead distance
	//       for safe manoeuvering, hence, the greater the computational cost.
	public class Missile : IEffect, ISync
	{
		readonly MissileInfo info;
		readonly ProjectileArgs args;
		readonly Animation anim;
		readonly string trailPalette;

		readonly WVec targetInaccuracyOffset;
		readonly WVec airburst;
		readonly bool lockOn;

		int elapsedTicks; // Elapsed ticks since launch (note: first call to Tick() happens immediately after launch!)

		ContrailRenderable contrail;
		int ticksToNextSmoke;

		// Parameters used in MoveMissile
		bool homingActivated;
		WVec homingDisplacement;
		WVec velocity;
		readonly WVec gravity;

		bool targetPassedBy;
		bool isTargetNearCliff;

		WPos targetPosition;
		[Sync] WPos missilePosition;
		int speed;

		int renderFacing;
		[Sync] int hFacing;
		[Sync] int vFacing;

		int lastCliffCount;
		List<int> distances, heights;

		public Actor SourceActor { get { return args.SourceActor; } }
		public Target GuidedTarget { get { return args.GuidedTarget; } }

		public Missile(MissileInfo info, ProjectileArgs args)
		{
			this.info = info;
			this.args = args;

			missilePosition = args.Source;
			airburst = new WVec(WDist.Zero, WDist.Zero, info.AirburstAltitude);
			gravity = new WVec(0, 0, -info.Gravity);

			var world = args.SourceActor.World;

			if (info.Inaccuracy.Length > 0)
			{
				var inaccuracy = Util.ApplyPercentageModifiers(info.Inaccuracy.Length, args.InaccuracyModifiers);
				targetInaccuracyOffset = WVec.FromPDF(world.SharedRandom, 2) * inaccuracy / 1024;
			}

			targetPosition = args.PassiveTarget + airburst + targetInaccuracyOffset;
			SetLaunchParameters(world, targetPosition - missilePosition, out hFacing, missilePosition, ref distances, ref heights, ref lastCliffCount,
				info, ref vFacing, ref isTargetNearCliff, ref velocity, ref homingDisplacement, ref homingActivated, out speed);

			if (world.SharedRandom.Next(100) <= info.LockOnProbability)
				lockOn = true;

			if (!string.IsNullOrEmpty(info.Image))
			{
				anim = new Animation(world, info.Image, () => renderFacing);
				anim.PlayRepeating(info.Sequence);
			}

			if (info.ContrailLength > 0)
			{
				var color = info.ContrailUsePlayerColor ? ContrailRenderable.ChooseColor(args.SourceActor) : info.ContrailColor;
				contrail = new ContrailRenderable(world, color, info.ContrailWidth, info.ContrailLength, info.ContrailDelay, info.ContrailZOffset);
			}

			trailPalette = info.TrailPalette;
			if (info.TrailUsePlayerPalette)
				trailPalette += args.SourceActor.Owner.InternalName;
		}

		public IEnumerable<IRenderable> Render(WorldRenderer wr)
		{
			if (info.ContrailLength > 0)
				yield return contrail;

			var world = args.SourceActor.World;
			if (!world.FogObscures(missilePosition))
			{
				if (info.Shadow)
				{
					var dat = world.Map.DistanceAboveTerrain(missilePosition);
					var shadowPos = missilePosition - new WVec(0, 0, dat.Length);
					var tempFacing = renderFacing;
					renderFacing = hFacing;
					foreach (var r in anim.Render(shadowPos, wr.Palette("shadow")))
						yield return r;
					renderFacing = tempFacing;
				}

				var palette = wr.Palette(info.Palette);
				foreach (var r in anim.Render(missilePosition, palette))
					yield return r;
			}
		}

		// Before each tick the following needs to be set:
		// - velocity if missile is in freefall mode,
		// - speed, hFacing, vFacing is missile is in homing mode
		public void Tick(World world)
		{
			// Move the missile, possibly decomposing the move into smaller ones
			var height = WDist.Zero;
			var displacement = WVec.Zero;
			MoveMissile(homingActivated, homingDisplacement, velocity, gravity, out displacement,
				missilePosition, out missilePosition, world, out height);

			elapsedTicks++;

			var targetDistanceVector = UpdateTargetPosition(args, lockOn,
				airburst, targetInaccuracyOffset, ref targetPosition, missilePosition);

			if (CheckExplode(world, missilePosition, height, targetDistanceVector, info, elapsedTicks, contrail, this, args))
				return;

			UpdateRenderables(anim, displacement, ref renderFacing, info, homingActivated,
				world, missilePosition, trailPalette, ref ticksToNextSmoke, ref contrail);

			UpdateMovementParameters(targetDistanceVector, info, elapsedTicks, ref homingActivated, ref speed, ref velocity, ref vFacing,
				ref hFacing, world, missilePosition, args, ref targetPassedBy, ref isTargetNearCliff, ref distances,
				ref lastCliffCount, ref heights, ref homingDisplacement, gravity);
		}

		/// <summary>Makes sure that the missile does not travel too far below ground by
		/// shortening, if necessary, the length of the last move. This avoids what rendering
		/// explosions far away from place of impact when missiles move at large speeds.</summary>
		static void MoveMissile(bool homingActivated, WVec homingDisplacement, WVec velocity, WVec gravity, out WVec displacement,
			WPos missilePosition, out WPos newMissilePosition, World world, out WDist height)
		{
			if (!WillMoveBelowGround(homingActivated, 1 << 7,
				homingDisplacement, velocity, gravity, out displacement,
				missilePosition, out newMissilePosition,
				world, out height))
				return;

			var lambdaHeight = WDist.Zero;
			var lambdaNewMissilePosition = WPos.Zero;
			var lambdaDisplacement = WVec.Zero;
			FindSmallest(0, 1 << 7, testDisplacementFraction => WillMoveBelowGround
				(homingActivated, testDisplacementFraction,
				homingDisplacement, velocity, gravity, out lambdaDisplacement,
				missilePosition, out lambdaNewMissilePosition,
				world, out lambdaHeight));
			displacement = lambdaDisplacement;
			newMissilePosition = lambdaNewMissilePosition;
			height = lambdaHeight;
			return;
		}

		/// <param name="displacementFraction">A value between 0 and 128</param>
		static bool WillMoveBelowGround(bool homingActivated, int displacementFraction,
			WVec homingDisplacement, WVec velocity, WVec gravity, out WVec displacement,
			WPos missilePosition, out WPos newMissilePosition,
			World world, out WDist height)
		{
			if (homingActivated)
				displacement = homingDisplacement * displacementFraction >> 7;
			else
				displacement = (velocity * displacementFraction >> 7) + (gravity * displacementFraction * displacementFraction / 2 >> 14);
			newMissilePosition = missilePosition + displacement;
			height = world.Map.DistanceAboveTerrain(newMissilePosition);
			return height.Length < 0;
		}

		/// <returns>Vector from missile position to target position</returns>
		static WVec UpdateTargetPosition(ProjectileArgs args, bool lockOn,
			WVec airburst, WVec targetInaccuracyOffset, ref WPos targetPosition,
			WPos missilePosition)
		{
			// Compute current distance from target position
			if (args.GuidedTarget.IsValidFor(args.SourceActor) && lockOn)
				targetPosition = args.GuidedTarget.CenterPosition + airburst + targetInaccuracyOffset;
			return targetPosition - missilePosition;
		}

		static bool CheckExplode(World world, WPos missilePosition, WDist height,
			WVec targetDistanceVector, MissileInfo info, int elapsedTicks,
			ContrailRenderable contrail, Missile instance, ProjectileArgs args)
		{
			// TODO: https://github.com/OpenRA/OpenRA/commit/871d328c357829e6769cb61d61285aa16e394d63#diff-f3450a5dcb328e0ab733404f5c74fc32R782
			var cell = world.Map.CellContaining(missilePosition);
			var shouldExplode = (height.Length < 0) // Hit the ground
				|| (targetDistanceVector.Length < info.CloseEnough.Length) // Within range
				|| (info.ExplodeWhenEmpty && info.RangeLimit != -1
					&& elapsedTicks > info.HomingActivationDelay + info.RangeLimit) // Ran out of fuel
				|| (info.Blockable && BlocksProjectiles.AnyBlockingActorAt(world, missilePosition)) // Hit a wall or other blocking obstacle
				|| !world.Map.Contains(cell) // This also avoids an IndexOutOfRangeException in GetTerrainInfo below.
				|| (!string.IsNullOrEmpty(info.BoundToTerrainType)
					&& world.Map.GetTerrainInfo(cell).Type != info.BoundToTerrainType) // Hit incompatible terrain
				|| (height.Length < info.AirburstAltitude.Length
					&& targetDistanceVector.HorizontalLength < info.CloseEnough.Length); // Airburst

			if (!shouldExplode)
				return false;

			Explode(info, world, missilePosition, contrail, instance, elapsedTicks, args);

			// TODO: Remove this
			if (height.Length < 0 && targetDistanceVector.Length >= info.CloseEnough.Length)
				return true;

			return true;
		}

		static void Explode(MissileInfo info, World world, WPos missilePosition,
			ContrailRenderable contrail, Missile instance, int elapsedTicks, ProjectileArgs args)
		{
			if (info.ContrailLength > 0)
				world.AddFrameEndTask(w => w.Add(new ContrailFader(missilePosition, contrail)));

			world.AddFrameEndTask(w => w.Remove(instance));

			// Don't blow up in our launcher's face!
			if (elapsedTicks <= info.Arm)
				return;

			args.Weapon.Impact(Target.FromPos(missilePosition), args.SourceActor, args.DamageModifiers);
		}

		static void UpdateRenderables(Animation anim, WVec displacement, ref int renderFacing,
			MissileInfo info, bool homingActivated, World world, WPos missilePosition,
			string trailPalette, ref int ticksToNextSmoke, ref ContrailRenderable contrail)
		{
			if (anim != null)
				anim.Tick();

			renderFacing = GetProjectedFacing(displacement);

			// Create the smoke trail effect
			if (!string.IsNullOrEmpty(info.TrailImage) && --ticksToNextSmoke < 0 && (homingActivated || info.TrailWhenDeactivated))
			{
				world.AddFrameEndTask(w => w.Add(new Smoke(w, missilePosition - 3 * displacement / 2, info.TrailImage, trailPalette, info.TrailSequence)));
				ticksToNextSmoke = info.TrailInterval;
			}

			if (info.ContrailLength > 0)
				contrail.Update(missilePosition);
		}

		// Sets homingActivated and (velocity or (speed and vFacing))
		static void UpdateMovementParameters(WVec targetDistanceVector, MissileInfo info, int elapsedTicks, ref bool homingActivated, ref int speed,
			ref WVec velocity, ref int vFacing, ref int hFacing, World world, WPos missilePosition,
			ProjectileArgs args, ref bool targetPassedBy, ref bool isTargetNearCliff, ref List<int> distances, ref int lastCliffCount, ref List<int> heights,
			ref WVec homingDisplacement, WVec gravity)
		{
			// Freefalling tick
			if (elapsedTicks < info.HomingActivationDelay || elapsedTicks > info.HomingActivationDelay + info.RangeLimit)
			{
				velocity += gravity;
				return;
			}

			SetHomingParameters(world, targetDistanceVector, info, missilePosition, args, ref hFacing, ref targetPassedBy, ref isTargetNearCliff,
				ref distances, ref lastCliffCount, ref heights, ref vFacing, ref speed, elapsedTicks, ref homingDisplacement, ref homingActivated,
				ref velocity);
		}

		// Sets velocity and hFacing
		static void SetLaunchParameters(World world, WVec targetDistanceVector, out int hFacing, WPos missilePosition, ref List<int> distances,
			ref List<int> heights, ref int lastCliffCount, MissileInfo info, ref int vFacing, ref bool isTargetNearCliff,
			ref WVec velocity, ref WVec homingDisplacement, ref bool homingActivated, out int speed)
		{
			hFacing = ArcTan(-targetDistanceVector.X, -targetDistanceVector.Y);

			speed = info.MaximumLaunchSpeed.Length;

			var minVFacing = Facing(info.MinimumLaunchAngle);
			var maxVFacing = Facing(info.MaximumLaunchAngle);
			var minSpeed = info.MinimumLaunchSpeed.Length;
			var maxSpeed = info.MaximumLaunchSpeed.Length;
			var targetRange = 3 * LoopRadius(speed, info.VerticalRateOfTurn); // NOTE: Possibly unhardcode
			ApplyMovementParameters(0, info, ref speed, hFacing, ref vFacing, ref homingDisplacement,
				out homingActivated, ref velocity, targetDistanceVector, world, missilePosition,
				minVFacing, maxVFacing, false, ref isTargetNearCliff, minSpeed, maxSpeed, targetRange, ref distances,
				ref heights, hFacing, ref lastCliffCount);
		}

		// Sets speed, hFacing and vFacing
		static void SetHomingParameters(World world, WVec targetDistanceVector, MissileInfo info, WPos missilePosition, ProjectileArgs args,
			ref int hFacing, ref bool targetPassedBy, ref bool isTargetNearCliff, ref List<int> distances, ref int lastCliffCount,
			ref List<int> heights, ref int vFacing, ref int speed, int elapsedTicks, ref WVec homingDisplacement,
			ref bool homingActivated, ref WVec velocity)
		{
			if (CheckJamming(info, world, missilePosition, args, targetDistanceVector, ref hFacing, ref vFacing))
				return;

			// Compute which horizontal direction the projectile should be facing
			var desiredHFacing = 0;
			if (args.GuidedTarget.IsValidFor(args.SourceActor))
				desiredHFacing = targetDistanceVector.Yaw.Facing;
			else
				desiredHFacing = hFacing;
			var oldHFacing = hFacing;
			hFacing = Util.TickFacing(hFacing, desiredHFacing, info.HorizontalRateOfTurn);

			// The sign that the target has been passed by is a sudden large change
			// (usually around 128) in the desired horizontal facing
			var targetJustPassedBy =
				Math.Abs((int)(sbyte)(desiredHFacing - hFacing))
				>= Math.Abs((int)(sbyte)(desiredHFacing + 128 - hFacing));
			if (targetJustPassedBy)
			{
				targetPassedBy = true;
				if (isTargetNearCliff)
					desiredHFacing += 128;
			}

			var minVFacing = (sbyte)((sbyte)vFacing - info.VerticalRateOfTurn);
			var maxVFacing = (sbyte)((sbyte)vFacing + info.VerticalRateOfTurn);
			var minSpeed = (speed - info.Acceleration.Length).Clamp(0, info.MaximumSpeed.Length);
			var maxSpeed = (speed + info.Acceleration.Length).Clamp(0, info.MaximumSpeed.Length);
			var targetRange = 3 * LoopRadius(speed, info.VerticalRateOfTurn); // NOTE: Possibly unhardcode
			ApplyMovementParameters(elapsedTicks, info, ref speed, hFacing, ref vFacing, ref homingDisplacement,
				out homingActivated, ref velocity, targetDistanceVector, world, missilePosition,
				minVFacing, maxVFacing, targetPassedBy, ref isTargetNearCliff, minSpeed, maxSpeed, targetRange,
				ref distances, ref heights, oldHFacing, ref lastCliffCount);
		}

		static bool CheckJamming(MissileInfo info, World world, WPos missilePosition, ProjectileArgs args, WVec targetDistanceVector,
			ref int hFacing, ref int vFacing)
		{
			// Check whether the homing mechanism is jammed
			var jammed = info.Jammable && world.ActorsWithTrait<JamsMissiles>().Any(tp => JammedBy(tp, missilePosition, args));
			if (!jammed)
				return false;

			// Compute which horizontal direction the projectile should be facing
			var desiredHFacing = hFacing + world.SharedRandom.Next(-info.JammedDiversionRange, info.JammedDiversionRange + 1);
			var desiredVFacing = vFacing + world.SharedRandom.Next(-info.JammedDiversionRange, info.JammedDiversionRange + 1);

			hFacing = Util.TickFacing(hFacing, desiredHFacing, info.HorizontalRateOfTurn);
			vFacing = Util.TickFacing(vFacing, desiredVFacing, info.VerticalRateOfTurn);

			return true;
		}

		static bool JammedBy(TraitPair<JamsMissiles> tp, WPos missilePosition, ProjectileArgs args)
		{
			if ((tp.Actor.CenterPosition - missilePosition).HorizontalLengthSquared > tp.Trait.Range.LengthSquared)
				return false;

			if (tp.Actor.Owner.Stances[args.SourceActor.Owner] == Stance.Ally && !tp.Trait.AlliedMissiles)
				return false;

			return tp.Actor.World.SharedRandom.Next(100 / tp.Trait.Chance) == 0;
		}

		static void ApplyMovementParameters(int elapsedTicks, MissileInfo info, ref int speed, int hFacing, ref int vFacing, ref WVec homingDisplacement,
			out bool homingActivated, ref WVec velocity, WVec targetDistanceVector, World world, WPos missilePosition,
			sbyte minVFacing, sbyte maxVFacing, bool targetPassedBy, ref bool isTargetNearCliff,
			int minSpeed, int maxSpeed, int targetRange, ref List<int> distances, ref List<int> heights, int oldHFacing, ref int lastCliffCount)
		{
			var targetDistance = targetDistanceVector.HorizontalLength;

			CheckTerrainHeights(hFacing, oldHFacing, ref distances, ref lastCliffCount,
				world, missilePosition, targetDistance, ref heights, vFacing, speed);

			var freefall = false;
			var upperTrajectory = false;
			ComputeMovementParameters(info, missilePosition.Z, targetDistance, targetDistanceVector.Z,
				minVFacing, maxVFacing, out vFacing, targetPassedBy, ref isTargetNearCliff,
				minSpeed, maxSpeed, out speed, elapsedTicks, targetRange, distances, heights, ref freefall, ref upperTrajectory);

			homingActivated = info.HomingActivationDelay <= elapsedTicks && elapsedTicks < info.HomingActivationDelay + info.RangeLimit;

			if (homingActivated)
			{
				homingDisplacement = VectorFromFacings(speed, hFacing, vFacing);
				return;
			}

			if (!freefall)
			{
				velocity = VectorFromFacings(speed, hFacing, vFacing);
				return;
			}

			var extraPrecision = 5;
			var speedSquared = (long)speed * speed;
			var targetDistanceSquared = targetDistanceVector.HorizontalLengthSquared;
			var targetElevationSquared = (long)targetDistanceVector.Z * targetDistanceVector.Z;
			var targetElevationGravitation = (long)info.Gravity * targetDistanceVector.Z;
			var discriminantSquareRoot = Exts.ISqrt(speedSquared * (speedSquared - 2 * targetElevationGravitation)
				- targetDistanceSquared * info.Gravity * info.Gravity);
			var verticalSpeedSquared = (speedSquared << extraPrecision << extraPrecision) / 2 +
				(speedSquared * targetElevationSquared + targetDistanceSquared * targetElevationGravitation
				+ (upperTrajectory ? 1 : -1) * targetDistanceSquared * discriminantSquareRoot << extraPrecision << extraPrecision)
				/ 2 / (targetDistanceSquared + targetElevationSquared);

			var verticalSpeed = (vFacing < 0 ? -1 : 1) * (int)Exts.ISqrt(verticalSpeedSquared);
			var ticksToImpact = (verticalSpeed + (int)Exts.ISqrt(verticalSpeedSquared
				- (2L * info.Gravity * targetDistanceVector.Z << extraPrecision << extraPrecision))) / info.Gravity;
			velocity = new WVec((targetDistanceVector.X << extraPrecision) / ticksToImpact,
				(targetDistanceVector.Y << extraPrecision) / ticksToImpact, verticalSpeed >> extraPrecision);
		}

		static void CheckTerrainHeights(int hFacing, int oldHFacing, ref List<int> distances, ref int lastCliffCount, World world,
			WPos missilePosition, int targetDistance, ref List<int> heights, int vFacing, int speed)
		{
			// Only probe the terrain anew if the horizontal facing has changed too much
			// Or a new cliff is coming up
			if (distances == null || Math.Abs(hFacing - oldHFacing) > 1 ||
				distances.Count > 1 && distances[1] < 512 && lastCliffCount - distances.Count > 0)
			{
				ProbeTerrainHeights(world, missilePosition, targetDistance, hFacing, out distances, out heights);
				lastCliffCount = distances.Count;
			}
			else
			{
				// Update distances of the surveyed terrain ahead of the missile
				// NOTE: Numerical correction
				var moveLength = Cos(vFacing, speed) * 1024 / 1022;
				for (var i = distances.Count - 1; i >= 0; i--)
				{
					if ((distances[i] -= moveLength) < 0)
					{
						distances.RemoveRange(0, i);
						heights.RemoveRange(0, i);
						distances[0] = 0;
						heights[0] = world.Map.MapHeight.Value[world.Map.CellContaining(missilePosition)] * 512;
						break;
					}
				}
			}
		}

		/// <param name="vFacing">Will be between minVFacing and maxVFacing.</param>
		/// <param name="speed">Will be between minSpeed and maxSpeed.</param>
		static void ComputeMovementParameters(MissileInfo info, int missilePositionZ, int targetDistance, int targetElevation,
			sbyte minVFacing, sbyte maxVFacing, out int vFacing, bool targetPassedBy, ref bool isTargetNearCliff,
			int minSpeed, int maxSpeed, out int speed, int elapsedTicks, int targetRange, List<int> distances, List<int> heights,
			ref bool freefall, ref bool upperTrajectory)
		{
			// Target passed by - allow missile to assume vertical facings below -64
			if (targetPassedBy && isTargetNearCliff)
			{
				var VFacing = 0;
				AimBackwardForTarget(targetElevation, targetDistance, minVFacing, maxVFacing, out VFacing, minSpeed, maxSpeed, out speed);
				vFacing = VFacing;
				return;
			}

			if (ShouldPrepareFreefall(info, elapsedTicks, targetDistance, maxSpeed, distances))
			{
				PrepareFreefall(targetDistance, targetElevation, distances, heights, missilePositionZ, info.Gravity,
					info.VerticalRateOfTurn, minVFacing, maxVFacing, out vFacing, minSpeed, maxSpeed, out speed, out freefall, ref upperTrajectory);
				return;
			}

			int terrainElevation;
			CheckAndAscendOverCliff(missilePositionZ, targetDistance, distances, heights, out terrainElevation,
				info.VerticalRateOfTurn, ref minVFacing, maxVFacing, minSpeed, ref maxSpeed);

			int missileAltitude, cliffDistance;
			if (ShouldDescendOverCliff(missilePositionZ, targetDistance, targetElevation, distances, heights,
				info.VerticalRateOfTurn, maxSpeed, out missileAltitude, out cliffDistance))
			{
				var VFacing = 0;
				DescendOverCliff(missileAltitude, cliffDistance, targetDistance, ref isTargetNearCliff,
					info.VerticalRateOfTurn, minVFacing, maxVFacing, out VFacing, minSpeed, maxSpeed, out speed);
				vFacing = VFacing;
				return;
			}

			// Target in range
			// TODO: Make missile avoid edge of cliff even when descent not necessary
			if (targetDistance <= targetRange)
			{
				var VFacing = 0;
				AimForTarget(targetDistance, targetElevation, targetPassedBy, info.VerticalRateOfTurn,
					minVFacing, maxVFacing, out VFacing, minSpeed, maxSpeed, out speed);
				vFacing = VFacing;
				return;
			}

			// Accelerate the missile
			speed = maxSpeed;

			// Aim to attain cruise altitude in next tick, if possible
			// Otherwise use the maximum vertical rate of turn upwards or downwards
			var VVFacing = ArcTan(terrainElevation + info.CruiseAltitude.Length, speed)
				.Clamp((sbyte)-info.VerticalRateOfTurn, (sbyte)info.VerticalRateOfTurn)
				.Clamp(minVFacing, maxVFacing);
			vFacing = VVFacing;
		}

		static void AimBackwardForTarget(int targetElevation, int targetDistance,
			sbyte minVFacing, sbyte maxVFacing, out int vFacing, int minSpeed, int maxSpeed, out int speed)
		{
			// Set vertical facing so that the missile faces its target
			vFacing = ArcTan(targetElevation, -targetDistance).Clamp(minVFacing, maxVFacing);

			// Accelerate if will not hurt targeting
			if (vFacing > minVFacing)
				speed = maxSpeed;
			else
				speed = minSpeed;
		}

		static void AimForTarget(int targetDistance, int targetElevation, bool targetPassedBy, int vrot,
			sbyte minVFacing, sbyte maxVFacing, out int vFacing, int minSpeed, int maxSpeed, out int speed)
		{
			// Set vertical facing so that the missile faces its target
			vFacing = ArcTan(targetElevation, targetDistance);

			// NOTE: Numerical correction
			// Vertical facing equal to minus one is considered a result of numerical innacuracy and invalid
			if (vFacing == -1)
				vFacing = 0;

			vFacing = vFacing.Clamp(minVFacing, maxVFacing);

			// Find greatest speed that would still allow hitting the target
			if (!targetPassedBy && targetElevation <= 0)
			{
				var lambdaVFacing = vFacing;
				speed = FindGreatest(minSpeed, maxSpeed, lambdaSpeed =>
					WillHitGroundTarget(targetDistance, targetElevation, lambdaVFacing,
					LoopRadius(lambdaSpeed, vrot))) ?? minSpeed;
			}
			else
				speed = maxSpeed;
		}

		/// <summary>
		/// Checks if a ground target is far away from the missile, allowing the missile enough space to curve downwards
		/// and hit it.
		/// </summary>
		static bool WillHitGroundTarget(int targetDistance, int targetElevation, int vFacing, int lambdaLoopRadius)
		{
			return Cathetus(Math.Max(0, Cos(vFacing, lambdaLoopRadius) + targetElevation), lambdaLoopRadius)
				+ Sin(vFacing, lambdaLoopRadius) <= targetDistance;
		}

		static bool ShouldPrepareFreefall(MissileInfo info, int elapsedTicks, int targetDistance, int maxSpeed, List<int> distances)
		{
			// Launching the missile and homing does not activate straight away
			if (elapsedTicks == 0 && info.HomingActivationDelay > 0)
				return true;

			// Missile will explode when fuel runs out or unlimited fuel => no need to prepare freefall
			if (info.ExplodeWhenEmpty || info.RangeLimit == -1)
				return false;

			var remainingTicks = info.HomingActivationDelay + info.RangeLimit - elapsedTicks;

			// Assume accelerated motion in each tick
			var acceleratedTicks = Math.Min(remainingTicks, (info.MaximumSpeed.Length - maxSpeed) / info.Acceleration.Length);

			var finalSpeed = maxSpeed + acceleratedTicks * info.Acceleration.Length;

			// Last cliff before target will be within loop radius at final speed from the final cliff
			var targetNearCliff = targetDistance - distances[distances.Count - 1] < LoopRadius(finalSpeed, info.VerticalRateOfTurn);

			// Estimate if target reachable using homing before the fuel runs out
			return maxSpeed * acceleratedTicks +
				info.Acceleration.Length * acceleratedTicks * acceleratedTicks +
				info.MaximumSpeed.Length * (remainingTicks - acceleratedTicks) < targetDistance +
				(targetNearCliff ? finalSpeed * 64 / info.VerticalRateOfTurn : 0);
		}

		// For each vertical facing there exists exactly one speed at which the target is on the missile's freefall trajectory.
		// Obviously, the vertical facing must at least be such that points directly from the missile's position to the target.
		// There exists a vertical facing, maximum range vertical facing, such that below it speeds decrease when increasing
		// vertical facings and above it speeds increase when increasing vertical facings.
		// TODO: FIX THIS!!!!
		static void PrepareFreefall(int targetDistance, int targetElevation, List<int> distances, List<int> heights, int missileHeight,
			int gravity, int vrot, int minVFacing, int maxVFacing, out int vFacing, int minSpeed, int maxSpeed, out int speed, out bool freefall,
			ref bool upperTrajectory)
		{
			// Minimum vertical facing for which there exists a speed with which the target is on the missile's freefall trajectory
			var minValidVFacing = ArcTan(targetElevation, targetDistance) + 1;

			// If the minimum vertical facing is not attainable, simply select heighest attainable vertical facing and greatest attainable speed
			if (maxVFacing < minValidVFacing)
			{
				vFacing = maxVFacing;
				speed = maxSpeed;
				freefall = false;
				return;
			}

			// Find smallest vertical facing that allows traveling over all the cliffs and obstacles posed by the terrain
			// This function never returns null since for the choice of vertical facing equal to 64
			// the missile is considered to freefall over terrain
			// When the target is out of range, this function will return 64
			var minAboveTerrainVFacing = FindSmallest(minValidVFacing, 64, lambdaVFacing =>
				WillFreefallOverTerrain(lambdaVFacing, missileHeight, targetDistance, targetElevation, heights, distances, gravity)).Value;

			// Find maximum range vertical facing
			var maxRangeVFacing = 32 + (minValidVFacing - 1) / 2;

			// Find smallest vertical facing such that the corresponding speed is <= maxSpeed
			// If this function returns null, maxSpeed is too low or equivalently, the target is too far away
			var minVFacing1 = FindSmallest(minAboveTerrainVFacing, maxRangeVFacing, lambdaVFacing =>
				ComputeInitialSpeed(lambdaVFacing, targetDistance, targetElevation, gravity) <= maxSpeed);

			// Find greatest vertical facing such that the corresponding speed is >= minSpeed
			// If this function returns null, minSpeed is too high or equivalently, the target is too close
			var maxVFacing1 = FindGreatest(minAboveTerrainVFacing, maxRangeVFacing, lambdaVFacing =>
				ComputeInitialSpeed(lambdaVFacing, targetDistance, targetElevation, gravity) >= minSpeed);

			if (minVFacing1 != null && minVFacing1 == minAboveTerrainVFacing && maxVFacing1 == null &&
				minVFacing < minAboveTerrainVFacing && minAboveTerrainVFacing <= maxVFacing)
			{
				vFacing = minAboveTerrainVFacing;
				speed = maxSpeed;
				freefall = true;
				upperTrajectory = false;
				return;
			}

			// There exist valid and attainable vertical facings
			if (minVFacing1 != null && maxVFacing1 != null
				&& minVFacing1 <= maxVFacing && minVFacing <= maxVFacing1)
			{
				// Use smallest attainable valid vertical facing
				vFacing = minVFacing1.Value.Clamp(minVFacing, maxVFacing);
				speed = ComputeInitialSpeed(vFacing, targetDistance, targetElevation, gravity);
				freefall = (minSpeed <= speed || maxVFacing1 == null) && speed <= maxSpeed;
				if (!freefall)
					speed = speed.Clamp(minSpeed, maxSpeed);
				upperTrajectory = false;
				return;
			}

			var maxRangeAboveTerrainVFacing = Math.Max(minAboveTerrainVFacing, maxRangeVFacing);

			// Find smallest vertical facing such that the corresponding speed is >= minSpeed
			// This function can never return null since for the choice of vertical facing equal to 64
			// the corresponding speed is infinitely large
			var minVFacing2 = FindSmallest(maxRangeAboveTerrainVFacing, 64, lambdaVFacing =>
				ComputeInitialSpeed(lambdaVFacing, targetDistance, targetElevation, gravity) >= minSpeed);

			// Find greatest vertical facing such that the corresponding speed is <= maxSpeed
			// If this function returns null, maxSpeed is too low or equivalently, the target is too far away
			var maxVFacing2 = FindGreatest(maxRangeAboveTerrainVFacing, 64, lambdaVFacing =>
				ComputeInitialSpeed(lambdaVFacing, targetDistance, targetElevation, gravity) <= maxSpeed);

			// If there exist valid vertical facings
			if (maxVFacing2 != null)
			{
				// Use smallest attainable valid vertical facing
				vFacing = minVFacing2.Value.Clamp(minVFacing, maxVFacing);
				speed = ComputeInitialSpeed(vFacing, targetDistance, targetElevation, gravity);
				freefall = minSpeed <= speed && speed <= maxSpeed;
				if (!freefall)
					speed = speed.Clamp(minSpeed, maxSpeed);
				upperTrajectory = true;
				return;
			}

			// This case happens when maxSpeed is insufficient to reach target (target out of reach)
			vFacing = maxRangeVFacing.Clamp(minVFacing, maxVFacing);
			speed = maxSpeed;
			freefall = false;
		}

		/// <summary>
		/// Checks if a freefalling projectile will successfully fly over all of the terrain below it without impacting it.
		/// </summary>
		static bool WillFreefallOverTerrain(int initialVFacing, int initialHeight, int targetDistance, int targetElevation,
			List<int> heights, List<int> distances, int gravity)
		{
			if (initialVFacing == 64)
				return true;

			var initialSpeed = ComputeInitialSpeed(initialVFacing, targetDistance, targetElevation, gravity);
			for (var i = 1; i < heights.Count; i++)
			{
				var distance = distances[i];
				var height = FreefallHeightAtDistance(initialVFacing, initialSpeed, distance, gravity)
					- distance / Cos(initialVFacing, initialSpeed); // NOTE: Numerical correction
				if (initialHeight + height < heights[i])
					return false;
			}

			return true;
		}

		/// <summary>
		/// Computes and returns the height at which a freefalling projectile would find itself at a given distance from launch
		/// position.
		/// </summary>
		static int FreefallHeightAtDistance(int initialVFacing, int initialSpeed, int distance, int gravity)
		{
			const int extraBits = 10;
			return (int)((Sin(2 * initialVFacing, distance << extraBits) -
				((long)distance * distance * gravity << extraBits) / initialSpeed / initialSpeed)
				/ ((1 << extraBits) + Cos(2 * initialVFacing, 1 << extraBits)));
		}

		/// <summary>
		/// Computes the appropriate launch speed for a given launch angle that would enable hitting the target at a certain
		/// distance and elevation.
		/// </summary>
		static int ComputeInitialSpeed(int initialVFacing, int targetDistance, int targetElevation, int gravity)
		{
			if (initialVFacing == 64)
				return int.MaxValue;

			const int extraBits = 10;
			var denominator = Sin(2 * initialVFacing, (long)targetDistance << extraBits)
				- ((targetElevation << extraBits) + Cos(2 * initialVFacing, (long)targetElevation << extraBits));

			return (int)Exts.ISqrt(((long)targetDistance * targetDistance * gravity << extraBits) / denominator);
		}

		static bool ShouldDescendOverCliff(int missileHeight, int targetDistance, int targetElevation,
			List<int> distances, List<int> heights, int vrot, int maxSpeed, out int missileAltitude, out int cliffDistance)
		{
			var loopRadius = LoopRadius(maxSpeed, vrot);
			if (distances.Count <= 1)
			{
				missileAltitude = 0;
				cliffDistance = 0;
				return false;
			}

			// Target above missile, cliff between the missile and the target and
			// aiming directly at the target will not impact the cliff
			cliffDistance = distances[distances.Count - 1];
			var farthestCliffHeight = heights[heights.Count - 1];
			missileAltitude = missileHeight - farthestCliffHeight;
			var cliffDescentPreparationDistance = 4 * loopRadius;
			return targetElevation < -missileAltitude && // Target below farthest cliff
				targetDistance - loopRadius < cliffDistance && // Target within one loop radius from farthest cliff
				targetDistance <= cliffDescentPreparationDistance && // Missile within distance to start preparing for descent
				targetDistance * missileAltitude < cliffDistance * -targetElevation; // Aiming at target directly would hit terrain
		}

		/// <summary>
		/// Attempts to find the greatest attainable speed for which there exists an attainable vertical facing that together
		/// assure successfully curving downward around the cliff's edge and hitting the target below it. It is assumed that
		/// the missile is currently above some high terrain from which it needs to descend and that between the missile and
		/// the target there is only the cliff whose edge needs to be avoided.
		/// </summary>
		static void DescendOverCliff(int missileAltitude, int cliffDistance, int targetDistance, ref bool attemptBackwardFlight,
			int vrot, sbyte minVFacing, sbyte maxVFacing, out int vFacing, int minSpeed, int maxSpeed, out int speed)
		{
			// Find highest speed that allows choosing a valid vertical facing that would
			// ensure successful descent over cliff & target hit
			sbyte outVFacing = 0;
			speed = FindGreatest(minSpeed, maxSpeed, lambdaSpeed =>
				WillHitTargetBelowCliff(missileAltitude, cliffDistance, targetDistance,
				LoopRadius(lambdaSpeed, vrot), minVFacing, maxVFacing, out outVFacing)) ?? minSpeed;
			vFacing = outVFacing;
			attemptBackwardFlight = true;
		}

		/// <summary>
		/// For a given speed (loop radius) tries to find an attainable corresponding vertical facing that would enable the
		/// missile to hit the target after curving downward over the cliff edge. If such a vertical facing is found, it is
		/// output and the function returns true. If no such vertical facing is found, the closest attainable one is output
		/// and the function returns false. Calling this function is valid when the missile is above a cliff.
		/// </summary>
		/// <param name="missileAltitude">May be negative if missile actually in front of and below the high terrain</param>
		/// <param name="targetDistance">May be less than cliffDistance to accommodate rough estimates from terrain
		/// height probing</param>
		static bool WillHitTargetBelowCliff(int missileAltitude, int cliffDistance, int targetDistance,
			int loopRadius, sbyte minVFacing, sbyte maxVFacing, out sbyte vFacing)
		{
			// Compute needed minimum altitude at loopRadius horizontal distance from target.
			// This is done by constructing a vertical loop of radius loopRadius
			// - whose center is horizontally at loopRadius away from the target and
			// - which touches the cliff's edge with its upper half
			// and then computing the altitude of the highest point of this circle.
			var criticalAltitude = loopRadius - Cathetus(loopRadius - (targetDistance - cliffDistance)
				.Clamp(0, loopRadius), loopRadius);

			// Compute missile's horizontal distance from the above loop's center
			var hDistance = loopRadius - targetDistance;

			// Missile's horizontal distance from target is greater than loopRadius
			if (hDistance <= 0)
			{
				// NOTE: Numerical correction
				criticalAltitude += loopRadius * 3 / 100;
				int ignore, finalVFacing = 0;
				sbyte lambdaVFacing = 0;
				var result = FindSmallest(minVFacing, maxVFacing, lambdaInitialVFacing =>
					CanAscendOverCliff(-hDistance, criticalAltitude - missileAltitude, lambdaInitialVFacing,
					loopRadius, out ignore, out lambdaVFacing, out finalVFacing));
				vFacing = lambdaVFacing.Clamp(minVFacing, maxVFacing);
				return result != null && finalVFacing == 0;
			}

			// If missile is strictly within the constructed vertical loop around cliff edge, then the target
			// cannot be hit. This means that the speed needs to be lowered.
			var isSpeedValid = missileAltitude >= criticalAltitude - (loopRadius - Cathetus(hDistance, loopRadius));

			// Find minimum vertical facing that the missile may have without hitting the cliff edge
			var minValidVFacing = -ArcTan(missileAltitude, cliffDistance);

			// Compute the vertical facing that the missile would have if it were traveling along the constructed loop
			vFacing = ArcSin(-hDistance, loopRadius);

			// Check if the vertical facing is valid and attainable
			if (minValidVFacing <= vFacing && minVFacing <= vFacing && vFacing <= maxVFacing)
				return isSpeedValid;

			vFacing = vFacing.Clamp(minVFacing, maxVFacing);
			return false;
		}

		/// <summary>
		/// Checks if a cliff is coming up that will need to be avoided by ascending above it. If it is, attempts to find
		/// the greatest attainable speed for which there exists an attainable vertical facing that together assure successfully
		/// avoiding the cliff and hitting the target. Then it sets that speed as the upper bound for speeds and the
		/// corresponding vertical facing as the lower bound for vertical facings. If no cliff is coming up, no changes are
		/// made to the maximum speed and minimum vertical facing.
		/// </summary>
		static void CheckAndAscendOverCliff(int missileHeight, int targetDistance, List<int> distances, List<int> heights,
			out int cliffElevation, int vrot, ref sbyte minVFacing, sbyte maxVFacing, int minSpeed, ref int maxSpeed)
		{
			cliffElevation = heights[0] - missileHeight;
			var loopRadius = LoopRadius(maxSpeed, vrot);
			var cliffAscentPreparationDistance = 2 * loopRadius;
			if (distances.Count <= 1 || distances[1] > cliffAscentPreparationDistance)
				return;

			var cliffDistance = 0;
			for (var i = 1; i < distances.Count; i++)
			{
				if (distances[i] > cliffAscentPreparationDistance)
					break;

				var elevation = heights[i] - missileHeight;
				if (elevation * cliffDistance < cliffElevation * distances[i])
					continue;

				cliffDistance = distances[i];
				cliffElevation = elevation;
			}

			if (cliffElevation <= 0)
				return;

			// Find highest speed that allows choosing a valid vertical facing that would
			// ensure successful ascent over cliff & target hit
			sbyte outMinVFacing = 0;
			sbyte lambdaMinVFacing = minVFacing;
			int lambdaCliffElevation = cliffElevation;
			maxSpeed = FindGreatest(minSpeed, maxSpeed, lambdaSpeed =>
				WillAscendAndHitTargetOverCliff(lambdaCliffElevation, cliffDistance, targetDistance,
				LoopRadius(lambdaSpeed, vrot), lambdaMinVFacing, maxVFacing, out outMinVFacing)) ?? minSpeed;
			minVFacing = outMinVFacing;
		}

		/// <summary>
		/// For a given speed (loop radius) tries to find the smallest attainable (initial) vertical facing for which there
		/// exists a corresponding mid vertical facing (which is to be attained using maximum vertical rate of turn and which
		/// should be kept until reaching the appropriate distance from the cliff, after which the vertical facing should be
		/// brought back down using the maximum vertical rate of turn; the appropriate distance here is defined as the distance
		/// from which the missile can reach the cliff's edge at minimum possible height with minimum possible non-negative
		/// vertical facing) that together allow the missile to climb the cliff and hit the target above it. If such a pair
		/// of initial and mid vertical facings exists, the function returns true and outputs the mid vertical facing. If it
		/// does not exist, the function returns false and outputs the mid vertical facing corresponding to initial vertical
		/// facing maxVFacing.
		/// </summary>
		static bool WillAscendAndHitTargetOverCliff(int cliffElevation, int cliffDistance, int targetDistance,
			int loopRadius, sbyte minVFacing, sbyte maxVFacing, out sbyte midVFacing)
		{
			// Slightly increase the height differential to avoid too low vertical facings due to numerical inaccuracy
			// NOTE: Numerical correction
			cliffElevation += loopRadius * 3 / 100;

			sbyte lambdaMidVFacing = 0;
			var result = FindSmallest(minVFacing, maxVFacing, lambdaInitialVFacing =>
				WillAscendAndHitTargetOverCliffInner(cliffDistance, cliffElevation,
				targetDistance, lambdaInitialVFacing, loopRadius, out lambdaMidVFacing));

			midVFacing = lambdaMidVFacing.Clamp(minVFacing, maxVFacing);
			return result != null;
		}

		/// <summary>
		/// 
		/// </summary>
		static bool WillAscendAndHitTargetOverCliffInner(int cliffDistance, int cliffElevation,
			int targetDistance, int initialVFacing, int loopRadius, out sbyte midVFacing)
		{
			// TODO: Try instead to find a critical greatest angle that allows hitting the target.
			// This means assuming expectedAltitude will always be around zero.
			int expectedAltitude, finalVFacing;
			if (!CanAscendOverCliff(cliffDistance, cliffElevation, (sbyte)initialVFacing,
				loopRadius, out expectedAltitude, out midVFacing, out finalVFacing))

				return false;

			// NOTE: This check is primarily here for ground targets
			// In the following consider the missile at the predicted height
			// above cliff's top
			// Compute missile's vertical distance from lower loop's center
			var missileElevation = Cos(finalVFacing, loopRadius);

			// Compute missile's horizontal distance from lower loop's center
			var missileDistance = -Sin(finalVFacing, loopRadius);

			// Compute terrain's vertical distance from lower loop's center
			var terrainElevation = Math.Max(missileElevation - expectedAltitude, 0);

			// Compute horizontal distance from lower loop's center at which
			// the missile would land if flying along the lower loop
			var terrainDistance = Cathetus(terrainElevation, loopRadius);

			// Compute horizontal distance from assumed missile position
			// to the position where it would land if flying along the lower loop
			var minRange = terrainDistance - missileDistance;

			// If this distance is greater than the target's horizontal
			// distance from the assumed missile position, the target cannot
			// be hit and the missile should thus be slowed down
			return minRange <= targetDistance - cliffDistance;
		}

		/// <summary>
		/// Checks if the missile can climb over the cliff without hitting it. If it can, returns true and the mid vertical
		/// facing that enables the successful ascent. If it cannot, returns false and maximum vertical facing that will be
		/// attained if increasing it using maximum vertical rate of turn before hitting the cliff.
		/// </summary>
		static bool CanAscendOverCliff(int cliffDistance, int cliffElevation, int initialVFacing,
			int loopRadius, out int expectedAltitude, out sbyte midVFacing, out int finalVFacing)
		{
			// Compute cliff's horizontal distance from lower loop's center
			var distMin = Sin(initialVFacing, loopRadius) - cliffDistance;

			// Compute minimum vertical facing that it is possible to attain before reaching the cliff
			// NOTE: Numerical correction with the + 1
			var minMidVFacing = distMin <= -loopRadius ? -64 : ArcSin(distMin, loopRadius) + 1;

			// Compute cliff's horizontal distance from upper vertical loop's center
			var distMax = Sin(initialVFacing, loopRadius) + cliffDistance;

			// Compute maximum vertical facing that it is possible to attain before reaching the cliff
			var maxMidVFacing = distMax >= loopRadius ? 64 : ArcSin(distMax, loopRadius);

			// minMidVFacing and maxMidVFacing ensure that calls to WillAscendOverCliff are valid
			int lambdaExpectedAltitude = 0, lambdaFinalVFacing = 0;
			var result = FindSmallest(minMidVFacing, maxMidVFacing, lambdaMidVFacing =>
				WillAscendOverCliff(cliffDistance, cliffElevation, initialVFacing, lambdaMidVFacing,
				loopRadius, out lambdaExpectedAltitude, out lambdaFinalVFacing));

			midVFacing = (sbyte)(result ?? maxMidVFacing);
			expectedAltitude = lambdaExpectedAltitude;
			finalVFacing = lambdaFinalVFacing;
			return result != null;
		}

		/// <summary>
		/// Checks if the missile will climb over the cliff without hitting it. Calling this function only makes sense when
		/// midVFacing is between 0 and 64. This function must only be called when the missile is far enough away from the
		/// cliff to change its vertical facing from initial vertical facing to mid vertical facing.
		/// Computes the minimum vertical facing with which the missile can reach the cliff as well as the altitude that the
		/// missile will have at that point. Calling this function only makes sense when midVFacing is between 0 and 64. This
		/// function must only be called when the missile is far enough away from the cliff to change its vertical facing from
		/// initial vertical facing to mid vertical facing.
		/// </summary>
		static bool WillAscendOverCliff(int cliffDistance, int cliffElevation,
			int initialVFacing, int midVFacing, int loopRadius, out int expectedAltitude, out int finalVFacing)
		{
			// Compute the horizontal distance traversed while changing the vertical facing
			// from initialVFacing to midVFacing
			var hDisplacement = Math.Abs(Sin(midVFacing, loopRadius) - Sin(initialVFacing, loopRadius));

			// Compute the vertical distance traversed while changing the vertical facing
			// from initialVFacing to midVFacing
			var vDisplacement = Math.Abs(Cos(midVFacing, loopRadius) - Cos(initialVFacing, loopRadius));

			var distanceToTraverse = cliffDistance - hDisplacement;

			// Horizontal distance needed in order to bring the vertical facing down to zero
			var distanceToLoopTop = Sin(midVFacing, loopRadius);

			// Height that the missile will gain while brining the vertical facing down to zero
			var heightToLoopTop = loopRadius - Cos(midVFacing, loopRadius);

			// If cannot attain zero vertical facing within distanceToTraverse
			if (distanceToLoopTop > distanceToTraverse)
			{
				// Horizontal distance needed after distanceToTraverse in order to bring
				// the vertical facing down to zero
				var newDistanceToLoopTop = distanceToLoopTop - distanceToTraverse;

				// Height above the vertical loop's center after traversing distanceToTraverse
				var newHeightAboveLoopCenter = Cathetus(newDistanceToLoopTop, loopRadius);

				// Gained height after traversing distanceToTraverse
				expectedAltitude = vDisplacement + newHeightAboveLoopCenter -
					(loopRadius - heightToLoopTop) - cliffElevation;

				// Attained vertical facing after traversing distanceToTraverse
				finalVFacing = ArcTan(newDistanceToLoopTop, newHeightAboveLoopCenter);
			}
			else
			{
				expectedAltitude = vDisplacement + heightToLoopTop - cliffElevation;

				if (midVFacing < 64)

					// Add height that the missile will gain over distanceToTraverse - distanceToLoopTop
					// without changing the vertical facing
					expectedAltitude += Tan(midVFacing, distanceToTraverse - distanceToLoopTop);
				else
					// Add any desired height needed to climb above the cliff,
					// possible since midVFacing == 64
					expectedAltitude = Math.Max(expectedAltitude, 0);

				// Attained vertical facing after traversing distanceToTraverse
				finalVFacing = 0;
			}

			// Check if the whole manoeuvre, i.e., attaining vertical facing midVFacing and then traveling
			// to the cliff while attempting to reach the cliff with zero vertical facing, will result
			// in flying over the cliff's top
			return expectedAltitude >= 0;
		}

		// TODO: Implement using geometry instead of polling
		static void ProbeTerrainHeights(World world, WPos missilePosition, int targetDistance, int hFacing,
			out List<int> distances, out List<int> heights)
		{
			heights = new List<int>();
			distances = new List<int>();

			distances.Add(0);
			var previousHeight = world.Map.MapHeight.Value[world.Map.CellContaining(missilePosition)] * 512;
			heights.Add(previousHeight);

			if (targetDistance == 0)
				return;

			// NOTE: Might be desired to unhardcode the lookahead step size
			const int fineStepSize = 16;
			const int coarseStepSize = 1024;
			const int firstCliffLookahead = 2048;
			const int lastCliffLookahead = 5120;
			int probeStepSize = fineStepSize;

			var targetHDistanceVector = VectorFromFacings(targetDistance, hFacing, 0);

			for (var probeDistance = probeStepSize; probeDistance < targetDistance + probeStepSize; probeDistance += probeStepSize)
			{
				if (probeDistance > targetDistance)
					probeDistance = targetDistance;

				var probePosition = missilePosition + targetHDistanceVector * probeDistance / targetDistance;

				// Once the lookahead leaves the map, jump out of the loop
				var probeCell = world.Map.CellContaining(probePosition);
				if (!world.Map.Contains(probeCell))
					continue;

				// Find hight at the currently probed position
				var probeHeight = world.Map.MapHeight.Value[probeCell] * 512;

				if (previousHeight != probeHeight)
				{
					if (previousHeight < probeHeight)
					{
						distances.Add(probeDistance - probeStepSize);
						heights.Add(probeHeight);
					}
					else
					{
						distances.Add(probeDistance);
						heights.Add(previousHeight);
					}

					previousHeight = probeHeight;
				}

				if (distances.Count == 2 && probeStepSize <= fineStepSize && probeDistance > firstCliffLookahead)
					probeStepSize = coarseStepSize;

				if (probeStepSize > fineStepSize && targetDistance - probeDistance - probeStepSize < lastCliffLookahead)
					probeStepSize = fineStepSize;
			}
		}

		static int LoopRadius(int speed, int rot)
		{
			// loopRadius in w-units = initialSpeed in w-units per tick / angular initialSpeed in radians per tick
			// angular initialSpeed in radians per tick = rot in facing units per tick * (pi radians / 128 facing units)
			// pi = 314 / 100
			// ==> loopRadius = (initialSpeed * 128 * 100) / (314 * rot)
			return speed * 6400 / 157 / rot;
		}

		static WVec VectorFromFacings(int length, int hFacing, int vFacing)
		{
			var hLength = (long)Cos(vFacing, -length);
			return new WVec((int)(Sin(hFacing, hLength)), (int)(Cos(hFacing, hLength)), Sin(vFacing, length));
		}

		/// <summary>
		/// Assuming that there exists an integer N between lowerBound and upperBound for which testCriterion(n) == true
		/// if and only if n between lowerBound and N, this function finds N. It may be assumed that the last call to
		/// testCriterion will be made with this N. In case no such N exists (testCriterion always returns false), then
		/// this function returns null and the last call to testCriterion is made with lowerBound.
		/// </summary>
		static int? FindGreatest(int lowerBound, int upperBound, Func<int, bool> testCriterion)
		{
			if (upperBound < lowerBound)
				return null;

			while (upperBound - lowerBound > 1)
			{
				var middle = (upperBound + lowerBound) / 2;
				if (testCriterion(middle))
					lowerBound = middle;
				else
					upperBound = middle - 1;
			}

			// At this point upperBound - lowerBound <= 1 (both cases possible)
			if (testCriterion(upperBound))
				return upperBound;

			if (upperBound != lowerBound && testCriterion(lowerBound))
				return lowerBound;

			return null;
		}

		/// <summary>
		/// Assuming that there exists an integer N between lowerBound and upperBound for which testCriterion(n) == true
		/// if and only if n between N and upperBound, this function finds N. It may be assumed that the last call to
		/// testCriterion will be made with this N. In case no such N exists (testCriterion always returns false), then
		/// this function returns null and the last call to testCriterion is made with upperBound.
		/// </summary>
		static int? FindSmallest(int lowerBound, int upperBound, Func<int, bool> testCriterion)
		{
			if (upperBound < lowerBound)
				return null;

			while (upperBound - lowerBound > 1)
			{
				var middle = (upperBound + lowerBound) / 2;
				if (testCriterion(middle))
					upperBound = middle;
				else
					lowerBound = middle + 1;
			}

			// At this point upperBound - lowerBound <= 1 (both cases possible)
			if (testCriterion(lowerBound))
				return lowerBound;

			if (lowerBound != upperBound && testCriterion(upperBound))
				return upperBound;

			return null;
		}

		static sbyte Facing(WAngle angle)
		{
			return (sbyte)(angle.Angle >> 2);
		}

		static sbyte ArcTan(int y, int x)
		{
			return Facing(WAngle.ArcTan(y, x));
		}

		static sbyte ArcSin(int y, int d)
		{
			return ArcTan(y, Cathetus(y, d));
		}

		static sbyte ArcCos(int x, int d)
		{
			return ArcTan(Cathetus(x, d), x);
		}

		static int Sin(int facing, int d)
		{
			return d * WAngle.FromFacing(facing).Sin() >> 10;
		}

		static long Sin(int facing, long d)
		{
			return d * WAngle.FromFacing(facing).Sin() >> 10;
		}

		static int Cos(int facing, int d)
		{
			return d * WAngle.FromFacing(facing).Cos() >> 10;
		}

		static long Cos(int facing, long d)
		{
			return d * WAngle.FromFacing(facing).Cos() >> 10;
		}

		static int CTan(int facing, int x)
		{
			return -Tan(facing + 64, x);
		}

		static long CTan(int facing, long x)
		{
			return -Tan(facing + 64, x);
		}

		static int Tan(int facing, int x)
		{
			return x * WAngle.FromFacing(facing).Tan() >> 10;
		}

		static long Tan(int facing, long x)
		{
			return x * WAngle.FromFacing(facing).Tan() >> 10;
		}

		static int Cathetus(int cathetus, int hypotenuse)
		{
			return Exts.ISqrt(hypotenuse * hypotenuse - cathetus * cathetus);
		}

		public static int GetProjectedFacing(WVec d)
		{
			return new WVec(d.X, d.Y - d.Z, 0).Yaw.Facing;
		}
	}
}
