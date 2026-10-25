"""Record one seamlessly looping lap of the Unitree G1 walking a circle (offline, dev-only).

What it does
    Runs Unitree's own trained G1 walking policy headless in MuJoCo with a constant
    [vx, vy, wz] command, so the robot walks a circle. The control loop is a faithful copy
    of unitree_rl_gym's deploy/deploy_mujoco/deploy_mujoco.py: PD torques every physics
    step, the policy every `control_decimation` steps (50 Hz), the 47-float observation
    (ang vel, gravity, command, joint pos/vel, last action, sin/cos of a 0.8 s gait phase)
    with the same scales. After a settle period it cuts a whole number N of gait periods
    closest to one revolution of heading (searching the start sample for the best closure),
    and writes a compact JSON clip at 50 Hz: pelvis position, pelvis quaternion and the 12
    leg joint angles, plus lap statistics and the loop-closure errors.

    With --fit-turn it also adjusts the turn rate wz so that the N periods turn the robot
    by exactly 2*pi (the policy's actual yaw rate is not exactly the commanded one). The
    fitted command is printed and stored in the clip, so re-running with
    `--cmd <that command>` reproduces the clip without fitting.

Inputs (a local clone of https://github.com/unitreerobotics/unitree_rl_gym, BSD-3-Clause)
    deploy/deploy_mujoco/configs/g1.yaml       dt, decimation, PD gains, default angles, scales
    deploy/pre_train/g1/motion.pt              TorchScript walking policy (LSTM, see simulate())
    resources/robots/g1_description/scene.xml  12-dof legs-only G1 MJCF (includes g1_12dof.xml)
    The clone is treated as data: no Python from it is imported (no legged_gym, no viewer);
    the policy is loaded with torch.jit.load and the model with mujoco.MjModel.

Requirements (dev-only, not used by the hub at runtime)
    Python 3.11, mujoco, torch (CPU build is fine), numpy, pyyaml; matplotlib for --plot.
        py -3.11 -m venv .venv-walk
        .venv-walk/Scripts/python -m pip install torch --index-url https://download.pytorch.org/whl/cpu
        .venv-walk/Scripts/python -m pip install mujoco numpy pyyaml matplotlib

Run
    git clone --depth 1 https://github.com/unitreerobotics/unitree_rl_gym
    .venv-walk/Scripts/python -I hub/cad/tools_offline/record_g1_walk.py \\
        --repo unitree_rl_gym --out walk_clip.json --plot walk_check.png
    The default --cmd 0.4 0 0.62471 is the committed clip's command (found with
    `--cmd 0.4 0 0.625 --fit-turn`: 14 gait periods = 11.2 s = one lap, radius ~0.67 m).
    Runs are deterministic on one setup (recorded with Python 3.11, mujoco 3.17.0,
    torch 2.14.1+cpu, numpy 2.4.6); other library versions may differ in the last digits.
    One run (about 32 s of simulated time) takes roughly 30 s on a laptop CPU.

Output JSON (compact): source, command, rate_hz, period_s, periods, samples, joints,
default_angles, angles [S][12] rad (absolute), pelvis_pos [S][3] m (world, Z up, as
recorded), pelvis_quat [S][4] (w,x,y,z, MuJoCo order), torques [S][12] N*m (applied joint
torque, i.e. after the model's force limits; see simulate()), actuator_limits [12] N*m
(|limit| per actuator, from the joints' MJCF `actuatorfrcrange`), model_mass_kg, lap {...},
closure {...}.
The S samples are the lap's control steps, the first included and the closing one
(which matches the first) excluded. The quaternion is continuous over the clip, so after
one full turn the closing sample's quaternion is about the NEGATIVE of the first one
(same rotation); interpolate with a shortest-path slerp across the loop seam.
"""

from __future__ import annotations

import argparse
import json
import math
import subprocess
import sys
import warnings
from pathlib import Path

import mujoco
import numpy as np
import torch
import yaml

REPO_URL = "https://github.com/unitreerobotics/unitree_rl_gym"
CONFIG_REL = "deploy/deploy_mujoco/configs/g1.yaml"
POLICY_REL = "deploy/pre_train/g1/motion.pt"
MODEL_REL = "resources/robots/g1_description/scene.xml"
DEPLOY_SCRIPT_REL = "deploy/deploy_mujoco/deploy_mujoco.py"

# deploy_mujoco.py hard-codes the gait phase period (it is not in the yaml).
GAIT_PERIOD_S = 0.8

# A sample counts as fallen if the pelvis drops below this or tilts more than this.
FALL_HEIGHT_M = 0.5
FALL_TILT_RAD = 0.5


# ---------------------------------------------------------------- deploy_mujoco.py, copied


def get_gravity_orientation(quaternion: np.ndarray) -> np.ndarray:
    """Gravity in the pelvis frame from a (w, x, y, z) quaternion, as in deploy_mujoco.py."""
    qw, qx, qy, qz = quaternion[0], quaternion[1], quaternion[2], quaternion[3]
    gravity_orientation = np.zeros(3)
    gravity_orientation[0] = 2 * (-qz * qx + qw * qy)
    gravity_orientation[1] = -2 * (qz * qy + qw * qx)
    gravity_orientation[2] = 1 - 2 * (qw * qw + qz * qz)
    return gravity_orientation


def pd_control(target_q, q, kp, target_dq, dq, kd):
    """Torques from position targets, as in deploy_mujoco.py."""
    return (target_q - q) * kp + (target_dq - dq) * kd


# ---------------------------------------------------------------- setup


def load_config(repo: Path, config_rel: str) -> dict:
    with open(repo / config_rel, "r", encoding="utf-8") as f:
        return yaml.safe_load(f)


def check_deploy_script(repo: Path) -> None:
    """Warn if the upstream loop no longer looks like the one this script copies."""
    path = repo / DEPLOY_SCRIPT_REL
    try:
        text = path.read_text(encoding="utf-8")
    except OSError:
        print(f"warning: {path} not found; cannot cross-check the control loop", file=sys.stderr)
        return
    for needle in (f"period = {GAIT_PERIOD_S}", "count = counter * simulation_dt",
                   "obs[9 + 3 * num_actions : 9 + 3 * num_actions + 2]",
                   "target_dof_pos = action * action_scale + default_angles"):
        if needle not in text:
            print(f"warning: '{needle}' not found in {DEPLOY_SCRIPT_REL}; upstream loop may "
                  "have changed, re-check this script against it", file=sys.stderr)


def repo_commit(repo: Path) -> str:
    try:
        out = subprocess.run(["git", "-C", str(repo), "rev-parse", "HEAD"],
                             capture_output=True, text=True, check=True)
        return out.stdout.strip()
    except (OSError, subprocess.CalledProcessError):
        return "unknown"


def leg_joints(m: mujoco.MjModel) -> tuple[list[str], list[int]]:
    """Names and qpos addresses of the hinge joints after the free joint, in model order."""
    assert m.jnt_type[0] == mujoco.mjtJoint.mjJNT_FREE and m.jnt_qposadr[0] == 0, \
        "expected the first joint to be the pelvis free joint"
    names, adrs = [], []
    for j in range(1, m.njnt):
        assert m.jnt_type[j] == mujoco.mjtJoint.mjJNT_HINGE
        names.append(mujoco.mj_id2name(m, mujoco.mjtObj.mjOBJ_JOINT, j))
        adrs.append(int(m.jnt_qposadr[j]))
    # deploy_mujoco.py uses d.qpos[7:], d.qvel[6:] and d.ctrl[:] positionally: check they line up.
    assert adrs == list(range(7, 7 + len(adrs))), f"unexpected qpos addresses {adrs}"
    assert m.jnt_dofadr[1:].tolist() == list(range(6, 6 + len(adrs))), "unexpected dof addresses"
    assert m.nq == 7 + len(adrs) and m.nv == 6 + len(adrs) and m.nu == len(adrs)
    for a in range(m.nu):
        assert m.actuator_trnid[a, 0] == a + 1, "actuator order does not match joint order"
    return names, adrs


def actuator_limits(m: mujoco.MjModel) -> list[float]:
    """|Torque| limit per actuator (joint order): the tightest clamp the sim applies.

    MuJoCo can clamp an actuator three ways: ctrlrange (x gear), forcerange, and the
    joint's actuatorfrcrange (which clamps qfrc_actuator, after actuator_force). The G1
    12-dof MJCF only sets the joint's actuatorfrcrange (m.jnt_actfrcrange).
    """
    limits = []
    for a in range(m.nu):
        j = m.actuator_trnid[a, 0]
        lim = math.inf
        if m.actuator_ctrllimited[a]:
            lim = min(lim, float(np.abs(m.actuator_ctrlrange[a] * m.actuator_gear[a, 0]).max()))
        if m.actuator_forcelimited[a]:
            lim = min(lim, float(np.abs(m.actuator_forcerange[a]).max()))
        if m.jnt_actfrclimited[j]:
            lo, hi = m.jnt_actfrcrange[j]
            assert lo == -hi, "asymmetric actuatorfrcrange"
            lim = min(lim, float(hi))
        limits.append(lim)
    return limits


# ---------------------------------------------------------------- simulation


def simulate(m: mujoco.MjModel, cfg: dict, policy_path: Path, cmd: np.ndarray, n_samples: int):
    """Run the deploy loop from the model's initial pose for n_samples control steps.

    The policy is recurrent (an LSTM whose hidden/cell state lives in module buffers and
    is updated on every call), so it is loaded fresh for each run, as deploy_mujoco.py does.

    Returns qpos [n_samples][nq], foot contact flags [n_samples][2] (left, right), and
    leg torques [n_samples][12] as applied and as requested, all sampled at each policy
    step right after the physics step it follows (qpos is the state the policy observed;
    the torques are the ones that acted during that last physics step). Applied torque is
    d.qfrc_actuator at the leg dofs: the joints' actuatorfrcrange clamps there, so
    d.actuator_force is the requested (pre-clamp) PD torque. They differ only when a joint
    saturates.
    """
    simulation_dt = cfg["simulation_dt"]
    control_decimation = cfg["control_decimation"]
    kps = np.array(cfg["kps"], dtype=np.float32)
    kds = np.array(cfg["kds"], dtype=np.float32)
    default_angles = np.array(cfg["default_angles"], dtype=np.float32)
    ang_vel_scale = cfg["ang_vel_scale"]
    dof_pos_scale = cfg["dof_pos_scale"]
    dof_vel_scale = cfg["dof_vel_scale"]
    action_scale = cfg["action_scale"]
    cmd_scale = np.array(cfg["cmd_scale"], dtype=np.float32)
    num_actions = cfg["num_actions"]
    num_obs = cfg["num_obs"]
    cmd = np.asarray(cmd, dtype=np.float32)
    with warnings.catch_warnings():  # torch 2.x flags jit.load as deprecated; it still works
        warnings.simplefilter("ignore", FutureWarning)
        policy = torch.jit.load(str(policy_path))

    action = np.zeros(num_actions, dtype=np.float32)
    target_dof_pos = default_angles.copy()
    obs = np.zeros(num_obs, dtype=np.float32)
    counter = 0

    d = mujoco.MjData(m)
    m.opt.timestep = simulation_dt

    floor = mujoco.mj_name2id(m, mujoco.mjtObj.mjOBJ_GEOM, "floor")
    feet = [mujoco.mj_name2id(m, mujoco.mjtObj.mjOBJ_BODY, n)
            for n in ("left_ankle_roll_link", "right_ankle_roll_link")]

    qpos_log = np.zeros((n_samples, m.nq))
    contact_log = np.zeros((n_samples, 2), dtype=bool)
    applied_log = np.zeros((n_samples, m.nu))
    requested_log = np.zeros((n_samples, m.nu))
    k = 0
    while k < n_samples:
        tau = pd_control(target_dof_pos, d.qpos[7:], kps, np.zeros_like(kds), d.qvel[6:], kds)
        d.ctrl[:] = tau
        mujoco.mj_step(m, d)

        counter += 1
        if counter % control_decimation == 0:
            qj = d.qpos[7:]
            dqj = d.qvel[6:]
            quat = d.qpos[3:7]
            omega = d.qvel[3:6]

            qj = (qj - default_angles) * dof_pos_scale
            dqj = dqj * dof_vel_scale
            gravity_orientation = get_gravity_orientation(quat)
            omega = omega * ang_vel_scale

            period = GAIT_PERIOD_S
            count = counter * simulation_dt
            phase = count % period / period
            sin_phase = np.sin(2 * np.pi * phase)
            cos_phase = np.cos(2 * np.pi * phase)

            obs[:3] = omega
            obs[3:6] = gravity_orientation
            obs[6:9] = cmd * cmd_scale
            obs[9: 9 + num_actions] = qj
            obs[9 + num_actions: 9 + 2 * num_actions] = dqj
            obs[9 + 2 * num_actions: 9 + 3 * num_actions] = action
            obs[9 + 3 * num_actions: 9 + 3 * num_actions + 2] = np.array([sin_phase, cos_phase])
            obs_tensor = torch.from_numpy(obs).unsqueeze(0)
            action = policy(obs_tensor).detach().numpy().squeeze()
            target_dof_pos = action * action_scale + default_angles

            qpos_log[k] = d.qpos
            applied_log[k] = d.qfrc_actuator[6:]
            requested_log[k] = d.actuator_force
            for i in range(d.ncon):
                g1, g2 = int(d.contact.geom1[i]), int(d.contact.geom2[i])
                other = g2 if g1 == floor else g1 if g2 == floor else -1
                if other >= 0:
                    body = m.geom_bodyid[other]
                    contact_log[k, 0] |= body == feet[0]
                    contact_log[k, 1] |= body == feet[1]
            k += 1
    return qpos_log, contact_log, applied_log, requested_log


# ---------------------------------------------------------------- analysis


def yaw_roll_pitch(quat: np.ndarray) -> tuple[np.ndarray, np.ndarray, np.ndarray]:
    """ZYX Euler angles (yaw unwrapped) from (w, x, y, z) quaternions [n][4]."""
    w, x, y, z = quat[:, 0], quat[:, 1], quat[:, 2], quat[:, 3]
    yaw = np.unwrap(np.arctan2(2 * (w * z + x * y), 1 - 2 * (y * y + z * z)))
    roll = np.arctan2(2 * (w * x + y * z), 1 - 2 * (x * x + y * y))
    pitch = np.arcsin(np.clip(2 * (w * y - z * x), -1.0, 1.0))
    return yaw, roll, pitch


def fit_circle(xy: np.ndarray) -> tuple[np.ndarray, float]:
    """Least-squares (Kasa) circle fit; returns centre and radius."""
    a = np.column_stack([2 * xy[:, 0], 2 * xy[:, 1], np.ones(len(xy))])
    b = (xy ** 2).sum(axis=1)
    cx, cy, c = np.linalg.lstsq(a, b, rcond=None)[0]
    return np.array([cx, cy]), float(math.sqrt(c + cx * cx + cy * cy))


def closure(qpos: np.ndarray, yaw: np.ndarray, i0: int, n: int) -> dict:
    """Errors between sample i0 and the closing sample i0 + n."""
    i1 = i0 + n
    return {
        "heading_deg": math.degrees((yaw[i1] - yaw[i0]) - 2 * math.pi * round((yaw[i1] - yaw[i0]) / (2 * math.pi))),
        "position_mm": 1000 * float(np.linalg.norm(qpos[i1, 0:3] - qpos[i0, 0:3])),
        "joint_max_deg": math.degrees(float(np.abs(qpos[i1, 7:] - qpos[i0, 7:]).max())),
    }


def check_upright(qpos: np.ndarray, roll: np.ndarray, pitch: np.ndarray, start: int) -> None:
    z = qpos[start:, 2]
    bad = (z < FALL_HEIGHT_M) | (np.abs(roll[start:]) > FALL_TILT_RAD) | (np.abs(pitch[start:]) > FALL_TILT_RAD)
    if bad.any():
        raise SystemExit(f"robot fell at sample {start + int(np.argmax(bad))} "
                         f"(z={z[np.argmax(bad)]:.3f} m); try a gentler command")


def cut_lap(qpos: np.ndarray, yaw: np.ndarray, settle: int, per: int, search: int,
            periods: int | None) -> tuple[int, int, float]:
    """Pick N periods (closest to one revolution unless given) and the best start sample.

    Returns (start, N, measured mean yaw rate rad/sample). The start is searched over
    `search` samples after `settle` to minimise heading deg + position cm + max joint deg.
    """
    rate = (yaw[-1] - yaw[settle]) / (len(yaw) - 1 - settle)
    if abs(rate) < 1e-6:
        raise SystemExit("robot is not turning; need a non-zero wz")
    n_per = periods or max(1, round(2 * math.pi / (abs(rate) * per)))
    n = n_per * per
    last_start = min(settle + search, len(yaw) - 1 - n)
    if last_start < settle:
        raise SystemExit("simulation too short for one lap; increase --sim-laps")
    best, best_cost = settle, float("inf")
    for i0 in range(settle, last_start + 1):
        e = closure(qpos, yaw, i0, n)
        cost = abs(e["heading_deg"]) + e["position_mm"] / 10 + e["joint_max_deg"]
        if cost < best_cost:
            best, best_cost = i0, cost
    return best, n_per, rate


def step_rate(contacts: np.ndarray, rate_hz: float) -> float:
    """Steps per second (both feet), from the mean interval between a foot's touchdowns."""
    intervals = []
    for f in range(2):
        touchdowns = np.flatnonzero(np.diff(contacts[:, f].astype(int)) == 1)
        intervals.extend(np.diff(touchdowns))
    if not intervals:
        return 0.0
    return 2 * rate_hz / float(np.mean(intervals))


# ---------------------------------------------------------------- output


def rounded(a: np.ndarray, decimals: int) -> list:
    return np.round(a, decimals).tolist()


def build_clip(commit: str, cmd, cfg, joints, qpos, contacts, applied, requested, limits,
               mass_kg: float, yaw, roll, pitch, start: int, n_per: int, per: int,
               rate_hz: float) -> tuple[dict, dict]:
    """The clip dict to write, and extra info for the console summary and the plot."""
    s = n_per * per
    lap = slice(start, start + s)
    xy = qpos[lap, 0:2]
    centre, radius = fit_circle(xy)
    theta = np.unwrap(np.arctan2(xy[:, 1] - centre[1], xy[:, 0] - centre[0]))
    duration = s / rate_hz
    swept = abs(theta[-1] - theta[0]) * s / (s - 1)  # extrapolate to the closing sample
    heading_change = math.degrees(yaw[start + s] - yaw[start])
    clip = {
        "source": {"repo": REPO_URL, "commit": commit, "policy": POLICY_REL,
                   "model": MODEL_REL, "license": "BSD-3-Clause"},
        "command": [round(float(v), 5) for v in cmd],
        "rate_hz": rate_hz,
        "period_s": GAIT_PERIOD_S,
        "periods": n_per,
        "samples": s,
        "joints": joints,
        "default_angles": [float(v) for v in cfg["default_angles"]],
        "angles": rounded(qpos[lap, 7:], 4),
        "pelvis_pos": rounded(qpos[lap, 0:3], 4),
        "pelvis_quat": rounded(qpos[lap, 3:7], 5),
        "torques": (np.round(applied[lap], 2) + 0.0).tolist(),  # + 0.0 turns -0.0 into 0.0
        "actuator_limits": [round(v, 4) for v in limits],
        "model_mass_kg": round(mass_kg, 4),
        "lap": {
            "duration_s": round(duration, 3),
            "heading_change_deg": round(heading_change, 3),
            "radius_m": round(radius, 4),
            "mean_speed_m_s": round(radius * swept / duration, 4),
            "step_rate_hz": round(step_rate(contacts[start:start + s + 1], rate_hz), 3),
            "pelvis_height_m": [round(float(qpos[lap, 2].min()), 4), round(float(qpos[lap, 2].max()), 4)],
        },
        "closure": {k: float(f"{v:.3g}") for k, v in closure(qpos, yaw, start, s).items()},
    }
    lim = np.array(limits)
    info = {"centre": centre, "start": start,
            "roll_deg": np.degrees([roll[lap].min(), roll[lap].max()]),
            "pitch_deg": np.degrees([pitch[lap].min(), pitch[lap].max()]),
            "torque_peak": np.abs(applied[lap]).max(axis=0),
            "torque_peak_requested": np.abs(requested[lap]).max(axis=0),
            "saturated_samples": (np.abs(requested[lap]) >= lim - 1e-9).sum(axis=0),
            "torque_applied_vs_requested": float(np.abs(applied[lap] - requested[lap]).max()),
            "torque_closure": float(np.abs(applied[start + s] - applied[start]).max())}
    return clip, info


def plot_clip(clip: dict, info: dict, path: Path) -> None:
    import matplotlib
    matplotlib.use("Agg")
    import matplotlib.pyplot as plt

    ink, ink2, grid, surface = "#0b0b0b", "#52514e", "#e4e3df", "#fcfcfb"
    series = ["#2a78d6", "#eb6834", "#1baf7a", "#eda100", "#e87ba4", "#008300"]
    pos = np.array(clip["pelvis_pos"])
    ang = np.degrees(np.array(clip["angles"]))
    t = np.arange(clip["samples"]) / clip["rate_hz"]
    centre = info["centre"]

    fig, axes = plt.subplots(1, 3, figsize=(16, 5), gridspec_kw={"width_ratios": [1, 1.2, 1.6]})
    fig.patch.set_facecolor(surface)
    for ax in axes:
        ax.set_facecolor(surface)
        ax.grid(True, color=grid, linewidth=0.8)
        ax.tick_params(colors=ink2, labelsize=9)
        for side in ("top", "right"):
            ax.spines[side].set_visible(False)
        for side in ("left", "bottom"):
            ax.spines[side].set_color(grid)

    ax = axes[0]
    ax.plot(pos[:, 0], pos[:, 1], color=series[0], linewidth=1.5)
    ax.plot(*centre, marker="+", color=ink2, markersize=10)
    ax.plot(pos[0, 0], pos[0, 1], marker="o", markersize=9, color=series[1],
            markeredgecolor=surface, markeredgewidth=2, linestyle="none")
    ax.annotate("start", (pos[0, 0], pos[0, 1]), textcoords="offset points", xytext=(8, 6),
                color=ink, fontsize=9)
    ax.set_aspect("equal", adjustable="datalim")
    ax.set_xlabel("x (m)", color=ink2)
    ax.set_ylabel("y (m)", color=ink2)
    ax.set_title(f"Pelvis path, top-down (R = {clip['lap']['radius_m']:.2f} m)",
                 color=ink, fontsize=11, loc="left")

    ax = axes[1]
    ax.plot(t, pos[:, 2], color=series[0], linewidth=1.5)
    ax.set_xlabel("time (s)", color=ink2)
    ax.set_ylabel("pelvis height (m)", color=ink2)
    ax.set_title("Pelvis height", color=ink, fontsize=11, loc="left")

    ax = axes[2]
    for j in range(6):
        ax.plot(t, ang[:, j], color=series[j], linewidth=1.5,
                label=clip["joints"][j].replace("left_", "").replace("_joint", ""))
    ax.set_xlabel("time (s)", color=ink2)
    ax.set_ylabel("angle (deg)", color=ink2)
    ax.set_title("Left leg joint angles", color=ink, fontsize=11, loc="left")
    ax.legend(loc="upper left", bbox_to_anchor=(1.01, 1.0), frameon=False, fontsize=9,
              labelcolor=ink)

    c = clip["command"]
    fig.suptitle(f"G1 walk clip: cmd [{c[0]}, {c[1]}, {c[2]}], {clip['periods']} periods, "
                 f"{clip['samples']} samples, {clip['lap']['duration_s']} s",
                 color=ink, fontsize=12, x=0.01, ha="left")
    fig.tight_layout()
    fig.savefig(path, dpi=110, facecolor=surface)
    plt.close(fig)


# ---------------------------------------------------------------- main


def record(m, cfg, policy_path, cmd, args, per, rate_hz, periods=None):
    settle = round(args.settle_s * rate_hz)
    search = round(args.search_s * rate_hz)
    lap_est = 2 * math.pi / max(abs(cmd[2]), 1e-3) * rate_hz
    n_samples = settle + search + round(args.sim_laps * lap_est) + 2 * per
    qpos, contacts, applied, requested = simulate(m, cfg, policy_path, cmd, n_samples)
    yaw, roll, pitch = yaw_roll_pitch(qpos[:, 3:7])
    check_upright(qpos, roll, pitch, settle)
    start, n_per, rate = cut_lap(qpos, yaw, settle, per, search, periods)
    return qpos, contacts, applied, requested, yaw, roll, pitch, start, n_per, rate


def main() -> None:
    p = argparse.ArgumentParser(description=__doc__.split("\n\n")[0],
                                formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--repo", type=Path, required=True, help="local clone of unitree_rl_gym")
    p.add_argument("--out", type=Path, required=True, help="output clip JSON")
    p.add_argument("--plot", type=Path, help="optional check figure (PNG)")
    p.add_argument("--cmd", type=float, nargs=3, default=[0.4, 0.0, 0.62471],
                   metavar=("VX", "VY", "WZ"), help="forward m/s, sideways m/s, turn rad/s")
    p.add_argument("--settle-s", type=float, default=10.0, help="walk this long before the lap")
    p.add_argument("--search-s", type=float, default=4.0,
                   help="search this window after settling for the best loop start")
    p.add_argument("--sim-laps", type=float, default=1.6,
                   help="simulate this many estimated laps after settle+search")
    p.add_argument("--periods", type=int, help="force N gait periods (default: closest to one lap)")
    p.add_argument("--fit-turn", action="store_true",
                   help="adjust wz so N periods turn exactly 360 deg")
    p.add_argument("--fit-tol-deg", type=float, default=0.02,
                   help="--fit-turn stops when the clip's heading change is this close to 360")
    p.add_argument("--config", default=CONFIG_REL, help="config yaml, relative to --repo")
    p.add_argument("--commit", help="commit hash to record (default: git rev-parse in --repo)")
    args = p.parse_args()

    repo = args.repo.resolve()
    cfg = load_config(repo, args.config)
    check_deploy_script(repo)
    m = mujoco.MjModel.from_xml_path(str(repo / MODEL_REL))
    joints, adrs = leg_joints(m)
    torch.set_num_threads(1)
    policy_path = repo / POLICY_REL

    control_dt = cfg["simulation_dt"] * cfg["control_decimation"]
    rate_hz = round(1.0 / control_dt)
    per = round(GAIT_PERIOD_S / control_dt)
    cmd = np.array(args.cmd, dtype=np.float64)
    print(f"joints (qpos {adrs[0]}..{adrs[-1]}): {', '.join(joints)}")

    (qpos, contacts, applied, requested, yaw, roll, pitch, start, n_per,
     rate) = record(m, cfg, policy_path, cmd, args, per, rate_hz, args.periods)
    print(f"cmd {cmd.tolist()}: yaw rate {rate * rate_hz:.4f} rad/s -> {n_per} periods, "
          f"heading over clip {math.degrees(yaw[start + n_per * per] - yaw[start]):.2f} deg")
    if args.fit_turn:
        # The yaw rate is close to proportional to wz, so scale wz by the heading miss.
        for it in range(8):
            heading = yaw[start + n_per * per] - yaw[start]
            if abs(abs(heading) - 2 * math.pi) < math.radians(args.fit_tol_deg):
                break
            cmd[2] = round(cmd[2] * 2 * math.pi / abs(heading), 5)
            (qpos, contacts, applied, requested, yaw, roll, pitch, start, _,
             rate) = record(m, cfg, policy_path, cmd, args, per, rate_hz, n_per)
            print(f"  fit {it + 1}: wz {cmd[2]:.5f} -> yaw rate {rate * rate_hz:.5f} rad/s, heading "
                  f"over clip {math.degrees(yaw[start + n_per * per] - yaw[start]):.3f} deg")

    limits = actuator_limits(m)
    clip, info = build_clip(repo_commit(repo) if args.commit is None else args.commit, cmd, cfg,
                            joints, qpos, contacts, applied, requested, limits,
                            float(m.body_mass.sum()), yaw, roll, pitch, start, n_per, per, rate_hz)
    args.out.parent.mkdir(parents=True, exist_ok=True)
    args.out.write_text(json.dumps(clip, separators=(",", ":")), encoding="utf-8")
    t0 = (info["start"] + 1) * control_dt  # sample k is taken after (k+1)*decimation steps
    print(f"wrote {args.out} ({args.out.stat().st_size} bytes): lap starts at sim t={t0:.2f} s, "
          f"gait phase {t0 % GAIT_PERIOD_S / GAIT_PERIOD_S:.3f}")
    print(f"lap {json.dumps(clip['lap'])}")
    print(f"closure {json.dumps(clip['closure'])}")
    print(f"roll deg [{info['roll_deg'][0]:.2f}, {info['roll_deg'][1]:.2f}], "
          f"pitch deg [{info['pitch_deg'][0]:.2f}, {info['pitch_deg'][1]:.2f}]")
    print(f"model mass {clip['model_mass_kg']} kg; torque closure {info['torque_closure']:.4f} N*m; "
          f"max |applied - requested| {info['torque_applied_vs_requested']:.4f} N*m")
    print(f"{'joint':24s} {'peak |tau|':>10s} {'limit':>6s} {'% limit':>7s} {'saturated':>9s}")
    for j, name in enumerate(joints):
        peak, lim = info["torque_peak"][j], limits[j]
        print(f"{name:24s} {peak:10.2f} {lim:6.0f} {100 * peak / lim:6.1f}% "
              f"{int(info['saturated_samples'][j]):9d}")
    if args.plot:
        args.plot.parent.mkdir(parents=True, exist_ok=True)
        plot_clip(clip, info, args.plot)
        print(f"wrote {args.plot}")


if __name__ == "__main__":
    main()
