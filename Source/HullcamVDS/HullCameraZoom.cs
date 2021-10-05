﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;

namespace MuMech
{
	public class MuMechModuleHullCameraZoom : MuMechModuleHullCamera
	{
		[KSPField]
		public float cameraFoVMax = 120;

		[KSPField]
		public float cameraFoVMin = 5;

		[KSPField]
		public float cameraZoomMult = 1.25f;

		[KSPAction("Zoom In")]
		public void ZoomInAction(KSPActionParam ap)
		{
			sActionFlags.zoomIn = true;
		}

		[KSPAction("Zoom Out")]
		public void ZoomOutAction(KSPActionParam ap)
		{
			sActionFlags.zoomOut = true;
		}

		public override void OnUpdate()
		{
			base.OnUpdate();

			if (vessel == null)
			{
				return;
			}

			if (!camActive || CameraManager.Instance.currentCameraMode != CameraManager.CameraMode.Flight)
				return;

			if (sActionFlags.zoomIn || GameSettings.ZOOM_IN.GetKeyDown() || (Input.GetAxis("Mouse ScrollWheel") > 0))
			{
				cameraFoV = Mathf.Clamp(cameraFoV / cameraZoomMult, cameraFoVMin, cameraFoVMax);
				sActionFlags.zoomIn = false;
			}
			if (sActionFlags.zoomOut || GameSettings.ZOOM_OUT.GetKeyDown() || (Input.GetAxis("Mouse ScrollWheel") < 0))
			{
				cameraFoV = Mathf.Clamp(cameraFoV * cameraZoomMult, cameraFoVMin, cameraFoVMax);
				sActionFlags.zoomOut = false;
			}
			if (MapView.MapIsEnabled) 
			{ 
				cameraFoV = Mathf.Clamp (cameraFoV / cameraZoomMult, cameraFoVMin, cameraFoVMax);
			}
		}
	}
}
