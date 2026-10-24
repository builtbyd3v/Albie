using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// The hub's mocks (hub: python -m cad.make_mocks) as assets, so the CAD module runs with the hub off: a few
/// stored versions of each design. Built by Forge > CAD > Build CAD Prefabs from Assets/_Forge/Mocks/cad/.
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
        public TextAsset flight;        // flight.json (GET /cad/flight), rocket only
        public TextAsset drawing;       // drawing.png.bytes (GET /cad/drawing.png)
    }

    [Serializable]
    public class Part
    {
        public string part;             // design id, the folder name: <part>/vN/
        public List<Version> versions = new List<Version>();
    }

    public TextAsset partList;          // parts.json (GET /cad/parts); its first design opens first
    public List<Part> parts = new List<Part>();
    public TextAsset regenFailed;

    public int IndexOf(string part) => parts.FindIndex(p => p.part == part);
}
