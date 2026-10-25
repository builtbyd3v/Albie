# Unitree G1: third-party data for the humanoid design

The humanoid design (`hub/cad/parts/humanoid.py`) is Unitree Robotics' G1 humanoid. Everything in this folder comes
from two of Unitree's repositories, both under the BSD 3-Clause License, Copyright (c) HangZhou YuShu TECHNOLOGY
CO.,LTD. ("Unitree Robotics"). Their licence texts are kept here: `LICENSE` (unitree_ros) and
`LICENSE-unitree_rl_gym`. Unitree's name and logo aren't used to promote anything; the logo plate is left out.

## Robot description: unitree_ros

- Source: https://github.com/unitreerobotics/unitree_ros, commit `15ebe53f4cd77e7d745ae83b9d7ff90e685e8998`,
  folder `robots/g1_description/`.
- `g1_29dof_with_hand_rev_1_0.urdf`: copied unchanged (29 joints plus two Dex3-1 hands; links, masses, centres of
  mass, joint axes, limits and torque ratings).
- `display.glb`, `outline.npz`, `parts.json`: made from that URDF's binary STL meshes and the matching MuJoCo model
  (`g1_29dof_with_hand_rev_1_0.xml`, for its two colour tones) by `hub/cad/tools_offline/prepare_g1.py`. What we
  changed:
  - each link's mesh split into its separate pieces (slivers folded into the biggest), 88 pieces in all;
  - simplified with quadric decimation: about 205,000 triangles for display (from 625,000) and 83,000 for drawings;
  - vertices split at creases (32°) with smooth normals; metres to millimetres; the URDF's visual origins applied;
  - `logo_link` (the logo plate) left out;
  - `parts.json` adds our part names, each link's tone and each piece's share of the link's surface area.

## Walking motion: unitree_rl_gym

- Source: https://github.com/unitreerobotics/unitree_rl_gym, commit `276801e46c5d433564f24658bac64f254b7d2d4b`.
- `walk_clip.json`: one lap of Unitree's trained G1 walking policy (`deploy/pre_train/g1/motion.pt`) driving the
  12-joint G1 (`resources/robots/g1_description/scene.xml`) in MuJoCo with the command forward 0.4 m/s, turn
  0.625 rad/s, recorded headless by `hub/cad/tools_offline/record_g1_walk.py` (the control loop of
  `deploy/deploy_mujoco/deploy_mujoco.py`). It holds the 12 leg joint angles, the pelvis pose and the applied joint
  torques at 50 Hz for 14 gait periods (11.2 s, one 0.67 m circle), cut so it loops.
