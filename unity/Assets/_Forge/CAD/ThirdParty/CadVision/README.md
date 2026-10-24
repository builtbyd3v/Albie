# cad-vision (third party, MIT)

Copied unchanged from https://github.com/BAAA-Hacks/cad-vision at commit `0eeb64ba17cae9db19e897606d4315c70ce796d0`. License: LICENSE.txt.

- `CadMetadata.cs`: parses and validates the GLB + metadata.json pair (schema 1.0 / 2.1). Our hub exports this format.
- `CADSelectionMask.shader` + `CADSelectionOutline.shader`: silhouette rim used for the "changed parts" glow.

Our own loader (`../../Scripts/CadModel.cs`) reuses cad-vision's idea of a glTFast instantiator that exposes GLB node index -> GameObject.
Don't edit these files; wrap them.
