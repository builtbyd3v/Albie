using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// The hub's mocks (hub: python -m cad.make_mocks) as assets, so the CAD module runs with the hub off.
/// Built by Forge > CAD > Build CAD Prefabs from Assets/_Forge/Mocks/cad/vN/.
/// </summary>
[CreateAssetMenu(menuName = "Forge/CAD Mock Set", fileName = "CadMocks")]
public class CadMockSet : ScriptableObject
{
    [Serializable]
    public class Version
    {
        public string label;
        public TextAsset glb;           // model.glb.bytes
        public TextAsset metadata;      // metadata.json
        public TextAsset parameters;    // params.json (GET /cad/params)
        public TextAsset modelUpdated;  // model_updated.json (the /ws message)
        public TextAsset flight;        // flight.json (GET /cad/flight)
    }

    public List<Version> versions = new List<Version>();
    public TextAsset regenFailed;
}
