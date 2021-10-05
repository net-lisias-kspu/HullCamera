﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;

public class MuMechModuleHullCamera : PartModule
{
	// TODO: Bugs:
	// - If the vessel is entirely destroyed with AllowMainCamera == false it's still possible to get stuck without a camera.
	//   When that happens the menus don't work and we're stuck.
	//   The part might be dead but not removed, so part management needs improving.

	// TODO: If we prevent cycling between different vessels, then it's a better experience to keep track of each vessels active camera.
	// TODO: Test action groups.
	// TODO: Look at describing what camera we're viewing from.

	// TODO: No-main-camera issues:
	// - Can't rename vessel
	// - Can't make crew reports
	// - Can't control from different places

	private const bool adjustMode = false;

	[KSPField]
	public Vector3 cameraPosition = Vector3.zero;

	[KSPField]
	public Vector3 cameraForward = Vector3.forward;

	[KSPField]
	public Vector3 cameraUp = Vector3.up;

	[KSPField]
	public string cameraTransformName = "";

	[KSPField]
	public float cameraFoV = 60;

	[KSPField(isPersistant = false)]
	public float cameraClip = 0.01f;

	[KSPField]
	public bool camActive = false; // Saves when we're viewing from this camera.

	[KSPField]
	public bool camEnabled = true; // Lets us skip cycling through cameras.

	[KSPField(isPersistant = false)]
	public string cameraName = "Hull";

	public static List<MuMechModuleHullCamera> sCameras = new List<MuMechModuleHullCamera>();

	// Keep track of the camera we're viewing from.
	// A null value represents using the main camera.
	public static MuMechModuleHullCamera sCurrentCamera = null;

	// One camera module is the designated input handler, all others ignore it.
	// A camera's destroy function clears this and we have to set another in the update routine.
	public static MuMechModuleHullCamera sCurrentHandler = null;

	// Stores the current flight camera.
	protected static FlightCamera sCam = null;

	// Takes a backup of the external camera.
	protected static Transform sOrigParent = null;
	protected static Quaternion sOrigRotation = Quaternion.identity;
	protected static Vector3 sOrigPosition = Vector3.zero;
	protected static float sOrigFov;
	protected static float sOrigClip;
	protected static Texture2D sOverlayTex = null;

	// Stores the intended action to allow it to be passed to the update function.
	// Is there a reason for the action being deferred until Update, or can they just call the same function?
	protected struct ActionFlags
	{
		public bool deactivateCamera;
		public bool nextCamera;
		public bool prevCamera;
		public bool zoomIn;
		public bool zoomOut;
	}
	protected static ActionFlags sActionFlags;

	#region Configuration

	public static KeyBinding CAMERA_NEXT = new KeyBinding(KeyCode.O);
	public static KeyBinding CAMERA_PREV = new KeyBinding(KeyCode.P);
	public static KeyBinding CAMERA_RESET = new KeyBinding(KeyCode.Escape);

	// Allows switching to the main camera.
	// The main camera will only be used if there aren't any camera parts to use.
	public static bool sAllowMainCamera = true;

	// If the main camera can be switched to, allows cycling to it via next/previous actions.
	public static bool sCycleToMainCamera = false;

	// Prevents cycling to cameras not on the active vessel.
	public static bool sCycleOnlyActiveVessel = false;

	// Whether to log things to the debug log.
	// This could be made into an integer that describes how many things to log.
	public static bool sDebugOutput = false;

	#endregion

	#region Static Initialization

	protected static void DebugOutput(object o)
	{
		if (sDebugOutput)
		{
			Debug.Log(o);
		}
	}

	//protected static bool sInit = false;

	protected static void StaticInit()
	{
		// Commented out so that we can reload the config by reloading a save file rather than restarting KSP.
		/*
		if (sInit)
		{
			return;
		}
		sInit = true;
		*/

		try
		{
			foreach (ConfigNode cfg in GameDatabase.Instance.GetConfigNodes("HullCameraVDSConfig"))
			{
				if (cfg.HasNode("CAMERA_NEXT"))
				{
					CAMERA_NEXT.Load(cfg.GetNode("CAMERA_NEXT"));
				}
				if (cfg.HasNode("CAMERA_PREV"))
				{
					CAMERA_PREV.Load(cfg.GetNode("CAMERA_PREV"));
				}
				if (cfg.HasNode("CAMERA_RESET"))
				{
					CAMERA_RESET.Load(cfg.GetNode("CAMERA_RESET"));
				}
				if (cfg.HasValue("CycleMainCamera"))
				{
					sCycleToMainCamera = Boolean.Parse(cfg.GetValue("CycleMainCamera"));
				}
				if (cfg.HasValue("AllowMainCamera"))
				{
					sAllowMainCamera = Boolean.Parse(cfg.GetValue("AllowMainCamera"));
				}
				if (cfg.HasValue("CycleOnlyActiveVessel"))
				{
					sCycleOnlyActiveVessel = Boolean.Parse(cfg.GetValue("CycleOnlyActiveVessel"));
				}
				if (cfg.HasValue("DebugOutput"))
				{
					sDebugOutput = Boolean.Parse(cfg.GetValue("DebugOutput"));
				}
			}
		} catch(Exception e)
		{
			print("Exception when loading HullCamera config: " + e.ToString());
		}

		Debug.Log(string.Format("CMC: {0} AMC: {1} COA: {2}", sCycleToMainCamera, sAllowMainCamera, sCycleOnlyActiveVessel));
	}

	#endregion

	protected static void SaveMainCamera()
	{
		DebugOutput("SaveMainCamera");

		sOrigParent = sCam.transform.parent;
		sOrigClip = Camera.main.nearClipPlane;
		sOrigFov = Camera.main.fieldOfView;
		sOrigPosition = sCam.transform.localPosition;
		sOrigRotation = sCam.transform.localRotation;
	}

	protected static void RestoreMainCamera()
	{
		DebugOutput("RestoreMainCamera");

		sCam.transform.parent = sOrigParent;
		sCam.transform.localPosition = sOrigPosition;
		sCam.transform.localRotation = sOrigRotation;
		Camera.main.nearClipPlane = sOrigClip;
		sCam.SetFoV(sOrigFov);
		if (FlightGlobals.ActiveVessel != null && HighLogic.LoadedScene == GameScenes.FLIGHT)
		{
			sCam.setTarget(FlightGlobals.ActiveVessel.transform);
		}
		sOrigParent = null;
		if (sCurrentCamera != null)
		{
			sCurrentCamera.camActive = false;
		}
		sCurrentCamera = null;
	}

	protected static void CycleCamera(int direction)
	{
		DebugOutput(String.Format("CycleMainCamera({0})", direction));

		// Find the next camera to switch to, deactivate the current camera and activate the new one.
		MuMechModuleHullCamera newCam = sCurrentCamera;

		// Iterates the number of cameras and returns as soon as a camera is chosen.
		// Then if no camera is chosen, restore main camera as a last-ditch effort.
		for (int i = 0; i < sCameras.Count + 1; i += 1)
		{
			int nextCam = sCameras.IndexOf(newCam) + direction;
			if (nextCam >= sCameras.Count || nextCam < 0)
			{
				if (sAllowMainCamera && sCycleToMainCamera)
				{
					if (sCurrentCamera != null)
					{
						sCurrentCamera.camActive = false;
						sCurrentCamera = null;
						RestoreMainCamera();
					}
					return;
				}
				nextCam = (direction > 0) ? 0 : sCameras.Count - 1;
			}
			newCam = sCameras[nextCam];
			if (sCycleOnlyActiveVessel && FlightGlobals.ActiveVessel != null && FlightGlobals.ActiveVessel != newCam.vessel)
			{
				continue;
			}
			if (newCam.camEnabled && newCam.part.State != PartStates.DEAD)
			{
				if (sCurrentCamera != null)
				{
					sCurrentCamera.camActive = false;
				}
				sCurrentCamera = newCam;
				sCurrentCamera.camActive = true;
				return;
			}
		}
		// Failed to find a camera including cycling back to the one we started from. Default to main as a last-ditch effort.
		if (sCurrentCamera != null)
		{
			sCurrentCamera.camActive = false;
			sCurrentCamera = null;
			RestoreMainCamera();
		}
	}

	protected static void LeaveCamera()
	{
		DebugOutput("LeaveCamera");

		if (sCurrentCamera == null)
		{
			return;
		}
		if (sAllowMainCamera)
		{
			RestoreMainCamera();
		}
		else
		{
			CycleCamera(1);
		}
	}

	protected void Activate()
	{
		DebugOutput("Activate");

		if (part.State == PartStates.DEAD)
		{
			return;
		}
		if (camActive)
		{
			if (sAllowMainCamera)
			{
				RestoreMainCamera();
			}
			else
			{
				CycleCamera(1);
			}
			return;
		}
		sCurrentCamera = this;
		camActive = true;
	}

	protected void DirtyWindow()
	{
		foreach (UIPartActionWindow w in GameObject.FindObjectsOfType(typeof(UIPartActionWindow)).Where(w => ((UIPartActionWindow)w).part == part))
		{
			w.displayDirty = true;
		}
	}

	#region Events

	// Note: Events show in the part menu in flight.

	[KSPEvent(guiActive = true, guiName = "Activate Camera")]
	public void ActivateCamera()
	{
		Activate();
	}

	[KSPEvent(guiActive = true, guiName = "Disable Camera")]
	public void EnableCamera()
	{
		if (part.State == PartStates.DEAD)
		{
			return;
		}
		camEnabled = !camEnabled;
		Events["EnableCamera"].guiName = camEnabled ? "Disable Camera" : "Enable Camera";
		DirtyWindow();
	}

	#endregion

	#region Actions

	// Note: Actions are available to action groups.

	[KSPAction("Activate Camera")]
	public void ActivateCameraAction(KSPActionParam ap)
	{
		Activate();
	}

	[KSPAction("Deactivate Camera")]
	public void DeactivateCameraAction(KSPActionParam ap)
	{
		sActionFlags.deactivateCamera = true;
	}

	[KSPAction("Next Camera")]
	public void NextCameraAction(KSPActionParam ap)
	{
		sActionFlags.nextCamera = true;
	}

	[KSPAction("Previous Camera")]
	public void PreviousCameraAction(KSPActionParam ap)
	{
		sActionFlags.prevCamera = true;
	}

	#endregion

	#region Callbacks

	public void Update()
	{
		// In the VAB
		if (vessel == null)
		{
			return;
		}

		if (sCurrentHandler == null)
		{
			sCurrentHandler = this;
		}

		if (CameraManager.Instance.currentCameraMode != CameraManager.CameraMode.Flight)
		{
			return;
		}

		if (sActionFlags.deactivateCamera || CAMERA_RESET.GetKeyDown() || GameSettings.CAMERA_NEXT.GetKeyDown())
		{
			LeaveCamera();
			sActionFlags.deactivateCamera = false;
		}
		if (sActionFlags.nextCamera || (sCurrentHandler == this && CAMERA_NEXT.GetKeyDown()))
		{
			CycleCamera(1);
			sActionFlags.nextCamera = false;
		}
		if (sActionFlags.prevCamera || (sCurrentHandler == this && CAMERA_PREV.GetKeyDown()))
		{
			CycleCamera(-1);
			sActionFlags.prevCamera = false;
		}

		/*
        if ((sCurrentCamera == this) && adjustMode)
		{
            if (Input.GetKeyUp(KeyCode.Keypad8))
            {
                cameraPosition += cameraUp * 0.1f;
            }
            if (Input.GetKeyUp(KeyCode.Keypad2))
            {
                cameraPosition -= cameraUp * 0.1f;
            }
            if (Input.GetKeyUp(KeyCode.Keypad6))
            {
                cameraPosition += cameraForward * 0.1f;
            }
            if (Input.GetKeyUp(KeyCode.Keypad4))
            {
                cameraPosition -= cameraForward * 0.1f;
            }
            if (Input.GetKeyUp(KeyCode.Keypad7))
            {
                cameraClip += 0.05f;
            }
            if (Input.GetKeyUp(KeyCode.Keypad1))
            {
                cameraClip -= 0.05f;
            }
            if (Input.GetKeyUp(KeyCode.Keypad9))
            {
                cameraFoV += 5;
            }
            if (Input.GetKeyUp(KeyCode.Keypad3))
            {
                cameraFoV -= 5;
            }
            if (Input.GetKeyUp(KeyCode.KeypadMinus))
            {
                print("Position: " + cameraPosition + " - Clip = " + cameraClip + " - FoV = " + cameraFoV);
            }
        }
        */
	}

	public void FixedUpdate()
	{
		// In the VAB
		if (vessel == null)
		{
			return;
		}

		if (part.State == PartStates.DEAD)
		{
			if (camActive)
			{
				LeaveCamera();
			}
			Events["ActivateCamera"].guiActive = false;
			Events["EnableCamera"].guiActive = false;
			camEnabled = false;
			camActive = false;
			DirtyWindow();
			CleanUp();
			return;
		}

		if (!sAllowMainCamera && sCurrentCamera == null && !vessel.isEVA)
		{
			camActive = true;
			sCurrentCamera = this;
		}

		if (!camActive)
		{
			return;
		}

		if (!camEnabled)
		{
			CycleCamera(1);
			return;
		}

		if (sCam == null)
		{
			sCam = FlightCamera.fetch;
			// No idea if fetch returns null in normal operation (i.e. when there isn't a game breaking bug going on already)
			// but the original code had similar logic.
			if (sCam == null)
			{
				return;
			}
		}

		// Either we haven't set sOriginParent, or we've nulled it when restoring the main camera, so we save it again here.
		if (sOrigParent == null)
		{
			SaveMainCamera();
		}

		sCam.setTarget(null);
		sCam.transform.parent = (cameraTransformName.Length > 0) ? part.FindModelTransform(cameraTransformName) : part.transform;
		sCam.transform.localPosition = cameraPosition;
		sCam.transform.localRotation = Quaternion.LookRotation(cameraForward, cameraUp);
		sCam.SetFoV(cameraFoV);
		Camera.main.nearClipPlane = cameraClip;

		// If we're only allowed to cycle the active vessel and viewing through a camera that's not the active vessel any more, then cycle to one that is.
		if (sCycleOnlyActiveVessel && FlightGlobals.ActiveVessel != null && FlightGlobals.ActiveVessel != vessel)
		{
			CycleCamera(1);
		}

		base.OnFixedUpdate();
	}

	public override void OnStart(StartState state)
	{
		StaticInit();

		// Reading camEnabled right away, so is something setting this value?
		// KSPFields are saving game state.
		// So this must also be called when we load the game too.
		if ((state != StartState.None) && (state != StartState.Editor))
		{
			if (!sCameras.Contains(this))
			{
				sCameras.Add(this);
				DebugOutput(String.Format("Added, now {0}", sCameras.Count));
			}
			vessel.OnJustAboutToBeDestroyed += CleanUp;
		}
		part.OnJustAboutToBeDestroyed += CleanUp;
		part.OnEditorDestroy += CleanUp;

		if (part.State == PartStates.DEAD)
		{
			Events["ActivateCamera"].guiActive = false;
			Events["EnableCamera"].guiActive = false;
		}
		else
		{
			Events["EnableCamera"].guiName = camEnabled ? "Disable Camera" : "Enable Camera";
		}

		base.OnStart(state);
	}

	public void CleanUp()
	{
		DebugOutput("Cleanup");
		if (sCurrentHandler == this)
		{
			sCurrentHandler = null;
		}
		if (sCurrentCamera == this)
		{
			// On destruction, revert to main camera so we're not left hanging.
			LeaveCamera();
		}
		if (sCameras.Contains(this))
		{
			sCameras.Remove(this);
			DebugOutput(String.Format("Removed, now {0}", sCameras.Count));
			// This happens when we're saving and reloading.
			if (sCameras.Count < 1 && sOrigParent != null)
			{
				sCurrentCamera = null;
				RestoreMainCamera();
			}
		}
	}

	public void OnDestroy()
	{
		CleanUp();
	}

	#endregion
}