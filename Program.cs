using System;
using BingMapsRESTToolkit;
using StereoKit;

class Program
{
	// You can get a Bing Maps API key here:
	// https://www.bingmapsportal.com/Application
	static private string ApiKey = "AgANbJTAVjCk4Xi68UHeJPzj02dFj372ibzCJCU99hXihIz_DkAJZqFf_b4V--uN";

	static BoundingBox[] locationQueries = new BoundingBox[] {
		Geo.LatLonBounds( 22,    -159.5, 20000), // LatLon of Kauai
		Geo.LatLonBounds( 36.3, -112.75, 10000), // LatLon of Grand Canyon
		Geo.LatLonBounds( 27.98,  86.92, 10000), // LatLon of Everest
		Geo.LatLonBounds(-13.16, -72.54, 10000), // LatLon of Machu Picchu
	};
	static int locationId = -1;

	static Terrain terrain;
	static float   terrainScale = 0.00004f;
	static Pose    terrainPose  = new Pose(0, 0, -0.5f, Quat.Identity);
	static Vec2    mapHeightCenter;
	static Vec3    mapHeightSize;
	static Vec2    mapColorCenter;
	static Vec3    mapColorSize;

	static Model pedestalModel;
	static Model compassModel;
	static Model widgetModel;
	static float uiAngle = 0;

	static Vec3 dragStart;
	static Vec3 dragWidgetStart;
	static bool dragActive;

	static Mesh     floorMesh;
	static Material floorMat;

	///////////////////////////////////////////

	static void Main(string[] args)
	{
		// Initialize the StereoKit application
		StereoKitApp.settings.assetsFolder = "Assets";
		if (!StereoKitApp.Initialize("StereoKit_BingMaps", Runtime.Flatscreen))
			Environment.Exit(1);

		Initialize();

		while (StereoKitApp.Step(() =>
		{
			floorMesh?.Draw(floorMat, Matrix.T(0,-1.5f,0));

			ShowTerrainWidget();
		}));

		StereoKitApp.Shutdown();
	}

	///////////////////////////////////////////

	static void Initialize()
	{
		pedestalModel = Model.FromFile("Pedestal.glb", Default.ShaderUI);
		compassModel  = Model.FromFile("Compass.glb");
		widgetModel   = Model.FromFile("MoveWidget.glb");

		terrain = new Terrain(64, 0.3f, 3);
		terrain.clipRadius = 0.3f;

		// Add a floor if we're in VR, and hide the hands if we're in AR!
		if (StereoKitApp.System.displayType == Display.Opaque) 
		{ 
			floorMesh = Mesh.GeneratePlane(new Vec2(10, 10));
			floorMat  = Default.Material.Copy();
			floorMat[MatParamName.DiffuseTex] = Tex.FromFile("floor.png");
			floorMat[MatParamName.TexScale  ] = 8;
		}
		else
		{
			Default.MaterialHand[MatParamName.ColorTint] = Color.Black;
			//Input.HandVisible(Handed.Max, false);
		}

		LoadLocation(0);
	}

	///////////////////////////////////////////

	static Vec3 CalcPedestalUIDir()
	{
		// Get the angle from the center of the pedestal to the user's head,
		// flatten it on the Y axis, and normalize it for angle calculations.
		Vec3 dir = Input.Head.position - terrainPose.position;
		dir = dir.XZ.Normalized().X0Y;

		// Use a 'sticky' algorithm for updating the angle of the UI. We snap
		// to increments of 60 degrees, but only do it after we've traveled 
		// 20 degrees into the next increment. This prevents the UI from
		// moving back and forth when the user is wiggling around at the edge
		// of a snap increment.
		const float snapAngle    = 60;
		const float stickyAmount = 20;
		float angle = dir.XZ.Angle();
		if (SKMath.AngleDist(angle, uiAngle) > snapAngle/2 + stickyAmount)
			uiAngle = (int)(angle/snapAngle) * snapAngle + snapAngle/2;

		// Turn the angle back into a direction we can use to position the
		// pedestal
		return Vec3.AngleXZ(uiAngle);
	}

	///////////////////////////////////////////

	static void ShowTerrainWidget()
	{
		float pedestalScale = terrain.clipRadius * 2;
		UI.AffordanceBegin("TerrainWidget", ref terrainPose, pedestalModel.Bounds*pedestalScale, false, UIMove.PosOnly);
		pedestalModel.Draw(Matrix.TS(Vec3.Zero, pedestalScale));

		Vec3 uiDir  = CalcPedestalUIDir();
		Pose uiPose = new Pose(uiDir * (terrain.clipRadius + 0.04f), Quat.LookDir(uiDir+Vec3.Up));
		compassModel.Draw(Matrix.TS(uiDir * (terrain.clipRadius + 0.01f) + Vec3.Up * 0.02f, 0.4f));
		UI.WindowBegin("TerrainOptions", ref uiPose, new Vec2(30,0) * Units.cm2m, false);

		// Show location buttons
		Vec2 btnSize = new Vec2(6, 3) * Units.cm2m;
		if (UI.Radio("Kauai",        locationId == 0, btnSize)) LoadLocation(0);
		UI.SameLine();
		if (UI.Radio("Grand Canyon", locationId == 1, btnSize)) LoadLocation(1);
		UI.SameLine();
		if (UI.Radio("Mt. Everest",  locationId == 2, btnSize)) LoadLocation(2);
		UI.SameLine();
		if (UI.Radio("Machu Picchu", locationId == 3, btnSize)) LoadLocation(3);

		// Scale slider to zoom in and out
		float uiScale = terrainScale;
		if (UI.HSlider("Scale", ref uiScale, 0.00003f, 0.00005f, 0, 27*Units.cm2m))
			SetScale(uiScale);

		UI.WindowEnd(); // End TerrainOptions

		ShowTerrain();

		UI.AffordanceEnd(); // End TerrainWidget
	}

	///////////////////////////////////////////

	static void ShowTerrain()
	{
		// The first part of this method is dragging the terrain itself around
		// on the pedestal! Then after that, we can draw it :)

		// Here we're getting hand information that we'll use to calculate
		// the user's hand drag action.
		Hand hand      = Input.Hand(Handed.Right);
		Vec3 widgetPos = Hierarchy.ToLocal(
			hand[FingerId.Index, JointId.Tip].position * 0.5f + 
			hand[FingerId.Thumb, JointId.Tip].position * 0.5f);
		bool handInVolume = widgetPos.y > 0
				&& widgetPos.XZ.Magnitude < terrain.clipRadius; // For speed, use MagnitudeSq and clipRadius^2

		if (dragActive || handInVolume) {
			// Render a little compass widget between the fingers, as an 
			// indicator that users can grab/pinch it to move the map.
			float activeMod = dragActive ? 1.5f : 1;
			widgetModel.Draw(Matrix.TS(widgetPos, activeMod), Color.White*activeMod);

			// UI.IsInteracting tells us if an existing UI element is active.
			// If so, we don't want to steal focus from it, and can ignore
			// this IsJustPinched.
			if (!UI.IsInteracting(Handed.Right) && hand.IsJustPinched) 
			{
				// Save the initial positions, so we can calculate the drag
				// vector relative to the start point.
				dragStart       = terrain.LocalPosition;
				dragWidgetStart = widgetPos;
				dragActive      = true;
			}

			if (dragActive && hand.IsPinched)
			{
				// Update the terrain based on the current drag amount.
				Vec3 newPos = dragStart + (widgetPos - dragWidgetStart);
				newPos.y = 0;
				terrain.LocalPosition = newPos;
			}

			// Done with dragging!
			if (hand.IsJustUnpinched)
				dragActive = false;
		}

		// Update and draw the terrain itself
		terrain.Update();
	}

	///////////////////////////////////////////

	static void SetScale(float newScale)
	{
		// Set the terrain dimensions with the new scale
		terrain.SetHeightmapDimensions(mapHeightSize  *newScale, mapHeightCenter*newScale);
		terrain.SetColormapDimensions (mapColorSize.XZ*newScale, mapColorCenter *newScale);

		// Bring out translation into geographical space, and then scale it
		// back down into the new scale
		Vec3 geoTranslation = terrain.LocalPosition / terrainScale;
		terrain.LocalPosition = geoTranslation * newScale;

		terrainScale = newScale;
	}

	///////////////////////////////////////////

	static void LoadLocation(int id)    
	{
		if (locationId == id)
			return;
		locationId = id;

		// Reset data first, set terrain data values back to default!
		terrain.SetColormapData (Default.Tex,      Vec2.Zero, Vec2.Zero);
		terrain.SetHeightmapData(Default.TexBlack, Vec3.Zero, Vec2.Zero);
		terrain.LocalPosition = Vec3.Zero;

		// Now request color and height data from the Bing Maps API, and when
		// it receives the results, store the values and setup the terrain!

		BingMaps.RequestColor(ApiKey, ImageryType.Aerial, locationQueries[id], (tex, size, center) => {
			mapColorSize   = size;
			mapColorCenter = center;
			terrain.SetColormapData(tex, size.XZ*terrainScale, center*terrainScale);
		}).ConfigureAwait(false);

		BingMaps.RequestHeight(ApiKey, locationQueries[id], (tex, size, center) => {
			mapHeightSize   = size;
			mapHeightCenter = center;
			terrain.SetHeightmapData(tex, size*terrainScale, center*terrainScale);
		}).ConfigureAwait(false);
	}
}
