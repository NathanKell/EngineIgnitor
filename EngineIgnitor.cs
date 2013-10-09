﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;

namespace EngineIgnitor
{
	public class ModuleEngineIgnitor : PartModule
	{
		public enum EngineIgnitionState
		{
			INVALID = -1,
			NOT_IGNITED = 0,
			HIGH_TEMP = 1,
			IGNITED = 2,
		}

		[System.Serializable]
		public class IgnitorResource
		{
			public string name;
			public int id;
			public float amount;
			public float currentAmount;

			public IgnitorResource()
			{ 
			}

			public void Load(ConfigNode node)
			{
				name = node.GetValue("name");
				id = name.GetHashCode();

				if (node.HasValue("amount"))
				{
					amount = Mathf.Max(0.0f, float.Parse(node.GetValue("amount")));
				}
			}

			public void Save(ConfigNode node)
			{
				node.AddValue("name", name);
				node.AddValue("amount", Mathf.Max(0.0f, amount));
			}
		}

		// We can ignite as many times as we want by default.
		// -1: Infinite. 0: Unavailable. 1~...: As is.
		[KSPField(isPersistant = true)]
		public float ignitionsAvailable = -1;

		[KSPField(isPersistant = false, guiActive = true, guiName = "Ignitions")]
		private string ignitionsAvailableString = "Infinite";

		// If we don't have thrust but we still have such temperature then it can auto-ignite when throttle up again.
		[KSPField(isPersistant = false)]
		public float autoIgnitionTemperature = 800;

		[KSPField(isPersistant = false, guiActive = true, guiName = "Auto-Ignite")]
		private string autoIgnitionState = "?/800";

		// In case we have multiple engines...
		[KSPField(isPersistant = false)]
		public int engineIndex = 0;

		// List of all engines. So we can pick the one we are corresponding to.
		private List<ModuleEngines> engines = new List<ModuleEngines>();

		// And that's it.
		private ModuleEngines engine = null;

		// A state for the FSM.
		[KSPField(isPersistant = false, guiActive = true, guiName = "Engine State")]
		private EngineIgnitionState engineState = EngineIgnitionState.INVALID;

		private StartState m_startState = StartState.None;
		public List<IgnitorResource> ignitorResources;

		public override void OnStart(StartState state)
		{
			m_startState = state;

			foreach (PartModule module in this.part.Modules)
			{
				if (module is ModuleEngines)
				{
					engines.Add(module as ModuleEngines);
				}
			}
			if (engines.Count > engineIndex)
				engine = engines[engineIndex];
			else
				engine = null;
		}

		public override void OnAwake()
		{
			if (ignitorResources == null)
				ignitorResources = new List<IgnitorResource>();
		}

		public override string GetInfo()
		{
			if (ignitionsAvailable != -1)
				return "Can ignite for " + ignitionsAvailable.ToString() + " time(s).\n";
			else
				return "Can ignite for infinite times.\n";
		}

		public override void OnUpdate()
		{
			if (m_startState == StartState.None || m_startState == StartState.Editor) return;

			if (ignitionsAvailable != -1)
				ignitionsAvailableString = ignitionsAvailable.ToString();
			else
				ignitionsAvailableString = "Infinite";

			if (part != null)
				autoIgnitionState = part.temperature.ToString("F1") + "/" + autoIgnitionTemperature.ToString("F1");
			else
				autoIgnitionState = "?/" + autoIgnitionTemperature.ToString("F1");

			if (m_startState == StartState.None || m_startState == StartState.Editor) return;
			if (engine == null) return;
			if (engine.allowShutdown == false) return;

			// Record old state.
			EngineIgnitionState oldState = engineState;
			// Decide new state.
			if (engine.requestedThrust == 0.0f || engine.engineShutdown == true)
			{
				if (engine.part.temperature >= autoIgnitionTemperature)
				{
					engineState = EngineIgnitionState.HIGH_TEMP;
				}
				else
				{
					engineState = EngineIgnitionState.NOT_IGNITED;
				}
			}
			else
			{
				engineState = EngineIgnitionState.IGNITED;
			}

			// This flag is for low-resource state.
			bool preferShutdown = false;

			// Here comes the state transition process.
			if (oldState == EngineIgnitionState.NOT_IGNITED && engineState == EngineIgnitionState.IGNITED)
			{
				// We need to consume one ignitor to light it up.
				if (ignitionsAvailable > 0 || ignitionsAvailable == -1)
				{
					if (ignitorResources.Count != 0)
					{
						// We need to check if we have all ignitor resources.
						float minPotential = 1.0f;
						foreach (IgnitorResource resource in ignitorResources)
						{
							resource.currentAmount = part.RequestResource(resource.id, resource.amount);
							minPotential = Mathf.Min(minPotential, resource.currentAmount / resource.amount);
						}

						bool ignited = ( UnityEngine.Random.Range(0.0f, 1.0f) <= minPotential );
						if (ignited == false)
						{
							engineState = EngineIgnitionState.NOT_IGNITED;
							
							// Low in resources. Prefer to shutdown. Otherwise the ignitor device will be expired.
							//if (minPotential < 0.95f)
							//	preferShutdown = true;

							// Always shutdown the engine if it fails to ignite. player can manually retry.
							preferShutdown = true;
						}
					}

					// The ignitor device has been used no matter the ignition is successful or not.
					if(ignitionsAvailable > 0)
						ignitionsAvailable--;
				}
				else if (ignitionsAvailable == 0)
				{
					// Oops.
					engineState = EngineIgnitionState.NOT_IGNITED;
				}
				else
				{ 
					// Oooooops.
					Debug.Log("Invalid IgnitionsAvaiable: " + ignitionsAvailable.ToString());
				}
			}
			else if (oldState == EngineIgnitionState.HIGH_TEMP && engineState == EngineIgnitionState.IGNITED)
			{ 
				// Yeah we can auto-ignite without consuming ignitor.
				engineState = EngineIgnitionState.IGNITED;
			}

			// Finally we need to handle the thrust generation. i.e. forcibly shutdown the engine when needed.
			if (engineState == EngineIgnitionState.NOT_IGNITED && (ignitionsAvailable == 0 || preferShutdown == true))
			{
				foreach (BaseEvent baseEvent in engine.Events)
				{
					Debug.Log("Engine's event: " + baseEvent.name);
					if (baseEvent.name.IndexOf("shutdown", StringComparison.CurrentCultureIgnoreCase) >= 0)
					{
						baseEvent.Invoke();
					}
				}
			}
		}

		public override void OnSave(ConfigNode node)
		{
			base.OnSave(node);
			foreach (IgnitorResource ignitorResource in ignitorResources)
			{
				ignitorResource.Save(node.AddNode("IGNITOR_RESOURCE"));
			}
		}

		public override void OnLoad(ConfigNode node)
		{
			base.OnLoad(node);
			foreach (ConfigNode subNode in node.nodes)
			{
				if (subNode.name == "IGNITOR_RESOURCE")
				{
					if (!subNode.HasValue("name") || !subNode.HasValue("amount"))
					{
						Debug.Log("Ignitor Resource must have \'name\' and \'amount\'.");
						continue;
					}
					IgnitorResource newIgnitorResource = new IgnitorResource();
					newIgnitorResource.Load(subNode);
					ignitorResources.Add(newIgnitorResource);
				}
			}
		}
	}
}
