"""Humanoid robot: the Unitree G1, 1.32 m and 34 kg, with 29 joints and two three-finger Dex3-1 hands, posed by its
joints. Will it stand? Save this file and it regenerates.

Not built from solids: the 88 parts are Unitree's own meshes (hub/cad/data/unitree_g1/, BSD-3-Clause, see
NOTICE.md there), placed by the robot's URDF kinematics (cad/robot.py) for the pose the parameters ask for. Masses,
centres of mass, joint axes, limits and motor ratings all come from the URDF.

Millimetres, Z up, feet on z=0, the robot facing -Y (the front), its left on +X. Every link's parts sit in an
assembly named after the joint it hangs from (left_knee_joint holds the shin, and the ankle and foot below it), so
turning one assembly carries the rest of the limb: that's how Unity plays the Walk demo (simulate_motion), a lap
of Unitree's own trained walking controller recorded in MuJoCo (walk_clip.json, unitree_rl_gym, BSD-3-Clause).
"""
import json
import math
from functools import lru_cache
from pathlib import Path

import numpy as np
from build123d import Color, Compound, Cylinder, Location
from scipy.spatial import ConvexHull

from cad import meshes, robot as rb
from cad.part_api import (Analysis, BuildResult, Constant, Demo, Dimension, Explode, ExplodeMove, FixedMass, Marker,
                          Metric, Motion, MotionJoint, Param, Verdict, band)

TITLE = "Humanoid robot"
DESCRIPTION = ("Unitree G1 humanoid (BSD-3, unitree_ros): 29 joints and two 3-finger hands, posed by its joints. "
               "Will it stand? Walk plays Unitree's own trained gait.")
GLYPH = "robot"

LEGS = ("*hip_*", "*thigh*", "*shin*", "*ankle*", "*foot*")
UPPER = ("torso*", "waist_*", "head*", "*shoulder*", "*upper_arm*", "*forearm*", "*wrist*", "*palm*")
ARMS = ("*shoulder*", "*upper_arm*", "*forearm*", "*wrist*", "*palm*", "*thumb*", "*index*", "*middle*")
FINGERS = ("*thumb*", "*index*", "*middle*")

PARAMS = [
    Param("squat", 0.0, 0.0, 150.0, "°", 1, label="Squat", group="Legs", parts=LEGS, dimension="height",
          help="Knee bend. Hips and ankles follow, so the feet stay flat and the body upright (until the ankles "
               "reach their 50° limit)."),
    Param("stance", 240.0, 240.0, 540.0, "mm", 5, label="Stance width", group="Legs", parts=LEGS,
          help="Feet apart, centre to centre (the hips roll out, the ankles roll back)."),
    Param("step", 0.0, 0.0, 450.0, "mm", 5, label="Step", group="Legs", parts=LEGS,
          help="Left foot forward, right foot back, both flat: a split stance."),
    Param("lean", 0.0, -15.0, 75.0, "°", 1, label="Lean forward", group="Torso", parts=UPPER,
          help="Bends forward at the waist (up to 30°), then at the hips. The hips stay over the feet."),
    Param("twist", 0.0, -60.0, 60.0, "°", 1, label="Twist", group="Torso", parts=UPPER,
          help="Turns the upper body at the waist."),
    Param("arms_forward", 0.0, -30.0, 150.0, "°", 1, label="Arms forward", group="Arms", parts=ARMS,
          help="Raises both arms forward at the shoulders (0 = hanging down)."),
    Param("arms_out", 6.0, 0.0, 90.0, "°", 1, label="Arms out", group="Arms", parts=ARMS,
          help="Raises both arms out to the sides.", dimension="shoulders"),
    Param("elbow", 25.0, 0.0, 150.0, "°", 1, label="Elbow bend", group="Arms",
          parts=("*forearm*", "*wrist*", "*palm*") + FINGERS, help="0 = straight arms."),
    Param("grip", 0.0, 0.0, 100.0, "%", 5, label="Grip", group="Hands", parts=FINGERS,
          help="Closes the three-finger hands (they close on a carried weight anyway)."),
    Param("carry", 0.0, 0.0, 20.0, "kg", 0.5, label="Carry", group="Hands", parts=("*weight*", "*palm*"),
          help="A steel weight in each hand (half each), hanging from the palms."),
]

# Whole poses: a preset sets every parameter, so it looks the same whatever came before.
PRESETS = {
    "Stand": {"squat": 0, "stance": 240, "step": 0, "lean": 0, "twist": 0, "arms_forward": 0, "arms_out": 6,
              "elbow": 25, "grip": 0, "carry": 0},
    "Deep squat": {"squat": 150, "stance": 240, "step": 0, "lean": 0, "twist": 0, "arms_forward": 0, "arms_out": 6,
                   "elbow": 25, "grip": 0, "carry": 0},
    "Squat, arms forward": {"squat": 130, "stance": 240, "step": 0, "lean": 0, "twist": 0, "arms_forward": 70,
                            "arms_out": 6, "elbow": 10, "grip": 0, "carry": 0},
    "Lean forward": {"squat": 0, "stance": 240, "step": 0, "lean": 60, "twist": 0, "arms_forward": 90, "arms_out": 6,
                     "elbow": 0, "grip": 0, "carry": 0},
    "Wide stance": {"squat": 30, "stance": 480, "step": 0, "lean": 0, "twist": 0, "arms_forward": 0, "arms_out": 20,
                    "elbow": 25, "grip": 0, "carry": 0},
    "Arms out": {"squat": 0, "stance": 280, "step": 0, "lean": 0, "twist": 0, "arms_forward": 0, "arms_out": 90,
                 "elbow": 0, "grip": 0, "carry": 0},
    "Mid-stride": {"squat": 20, "stance": 240, "step": 380, "lean": 0, "twist": 15, "arms_forward": 0,
                   "arms_out": 6, "elbow": 25, "grip": 0, "carry": 0},
    "Carry 5 kg": {"squat": 0, "stance": 260, "step": 0, "lean": 0, "twist": 0, "arms_forward": 0, "arms_out": 6,
                   "elbow": 80, "grip": 100, "carry": 5},
    "Carry 15 kg": {"squat": 0, "stance": 260, "step": 0, "lean": 0, "twist": 0, "arms_forward": 0, "arms_out": 6,
                    "elbow": 80, "grip": 100, "carry": 15},
    "Carry 10 kg, arms down": {"squat": 0, "stance": 260, "step": 0, "lean": 0, "twist": 0, "arms_forward": 0,
                               "arms_out": 6, "elbow": 10, "grip": 100, "carry": 10},
}

DATA = Path(__file__).resolve().parents[1] / "data" / "unitree_g1"
URDF = DATA / "g1_29dof_with_hand_rev_1_0.urdf"
SIDES = (("left", 1.0), ("right", -1.0))
FACE = rb.axis_angle((0, 0, 1), -math.pi / 2)   # URDF faces +X; the part faces -Y (its left on +X)
G = rb.G

DARK, LIGHT, MID, STEEL = (0.11, 0.12, 0.14), (0.80, 0.81, 0.83), (0.40, 0.42, 0.45), (0.30, 0.34, 0.40)
STEEL_DENSITY = 7.85            # g/cm^3, the carried weights
WEIGHT_D, HANDLE_D, HANDLE_L = 100.0, 16.0, 70.0   # mm
SOLE_TOL = 6.0                  # mm: sole points this close to the floor carry weight
WAIST_PITCH_MAX = math.radians(30)
ARM_SWING = 3.0                 # Walk: shoulder pitch swing per radian of same-side hip pitch (opposite sense)

BALANCE_BANDS = ((0.0, "bad"), (25.0, "warn"), (120.0, "ok"))
LOAD_BANDS = ((60.0, "ok"), (90.0, "warn"), (150.0, "bad"))
ASSUMPTIONS = [
    "Static: the pose held still, gravity only. Masses, centres of mass and joint axes from Unitree's URDF.",
    "Motor ratings are the URDF's peak torques; holding a pose for long takes less (motors heat up).",
    "Both feet on the ground share the weight by the lever rule; the hips stay over the feet when it leans.",
]


# The robot and its parts

@lru_cache(maxsize=1)
def robot() -> rb.Robot:
    return rb.load(URDF)


def pack() -> meshes.Pack:
    return meshes.pack(DATA)


@lru_cache(maxsize=1)
def soles() -> dict[str, np.ndarray]:
    """Each foot's sole outline (convex hull points) in its link frame: the outline mesh's lowest points."""
    out = {}
    for side, _ in SIDES:
        pieces = pack().manifest["links"][f"{side}_ankle_roll_link"]["pieces"]
        v = np.vstack([pack().outlines[p["key"]][0] for p in pieces])
        low = v[v[:, 2] < v[:, 2].min() + 3.0]
        out[side] = low[ConvexHull(low[:, :2]).vertices]
    return out


def homogeneous(points: np.ndarray, m: np.ndarray) -> np.ndarray:
    return points @ m[:3, :3].T + m[:3, 3]


# The pose

class Pose:
    """Joint angles for the parameters, and where every link sits (part mm) with the feet flat on z=0."""

    def __init__(self, p: dict):
        self.p = p
        roll = stride = 0.0
        for _ in range(2):   # each changes the other a little
            roll = self.solve(lambda r: self.width(self.angles_for(p, r, stride)), p["stance"], 0.0, 0.25)
            stride = self.solve(lambda a: self.stride(self.angles_for(p, roll, a)), p["step"], 0.0, 0.2)
        self.angles, self.clamped = robot().clamp(self.angles_for(p, roll, stride))
        self.base = self.ground(self.angles)
        self.poses = robot().poses(self.angles, self.base)
        self.weights = self.carried()

    @staticmethod
    def angles_for(p: dict, roll: float, stride: float) -> dict[str, float]:
        k = math.radians(p["squat"])
        lean = math.radians(p["lean"])
        waist = max(-WAIST_PITCH_MAX, min(WAIST_PITCH_MAX, lean))
        grip = max(p["grip"] / 100, 0.8 if p["carry"] > 0 else 0.0)
        out = {"waist_yaw_joint": math.radians(p["twist"]), "waist_pitch_joint": waist}
        for side, s in SIDES:
            forward = stride if side == "left" else -stride
            out |= {f"{side}_hip_pitch_joint": -k / 2 - (lean - waist) - forward, f"{side}_knee_joint": k,
                    f"{side}_ankle_pitch_joint": -k / 2 + forward,
                    f"{side}_hip_roll_joint": s * roll, f"{side}_ankle_roll_joint": -s * roll,
                    f"{side}_shoulder_pitch_joint": -math.radians(p["arms_forward"]),
                    f"{side}_shoulder_roll_joint": s * math.radians(p["arms_out"]),
                    f"{side}_elbow_joint": math.radians(90 - p["elbow"]),
                    f"{side}_hand_index_0_joint": -s * 1.25 * grip, f"{side}_hand_index_1_joint": -s * 1.35 * grip,
                    f"{side}_hand_middle_0_joint": -s * 1.25 * grip, f"{side}_hand_middle_1_joint": -s * 1.35 * grip,
                    f"{side}_hand_thumb_1_joint": s * 0.5 * grip, f"{side}_hand_thumb_2_joint": s * 1.2 * grip}
        return out

    @staticmethod
    def feet(angles: dict[str, float]) -> dict[str, np.ndarray]:
        """Each foot's frame in the pelvis frame."""
        poses = robot().poses(angles)
        return {side: poses[f"{side}_ankle_roll_link"] for side, _ in SIDES}

    @classmethod
    def ground(cls, angles: dict[str, float]) -> np.ndarray:
        """The pelvis frame in part space: turned so the feet lie flat (their mean tilt undone) facing -Y, then
        moved so the soles touch z=0 and the point between the feet is on the Z axis."""
        feet = cls.feet(angles)
        u, _, vt = np.linalg.svd(sum(f[:3, :3] for f in feet.values()))
        base = rb.transform(FACE @ (u @ vt).T)
        points = np.vstack([homogeneous(soles()[side], base @ f) for side, f in feet.items()])
        centre = np.mean([homogeneous(soles()[side], base @ f).mean(axis=0) for side, f in feet.items()], axis=0)
        base[:3, 3] = (-centre[0], -centre[1], -points[:, 2].min())
        return base

    @classmethod
    def width(cls, angles) -> float:
        feet = cls.feet(angles)
        base = cls.ground(angles)
        c = {side: homogeneous(soles()[side], base @ f).mean(axis=0) for side, f in feet.items()}
        return float(c["left"][0] - c["right"][0])

    @classmethod
    def stride(cls, angles) -> float:
        feet = cls.feet(angles)
        base = cls.ground(angles)
        c = {side: homogeneous(soles()[side], base @ f).mean(axis=0) for side, f in feet.items()}
        return float(c["right"][1] - c["left"][1])   # the left foot ahead (-Y)

    @staticmethod
    def solve(measure, target: float, x0: float, x1: float) -> float:
        """The joint angle whose measure hits target: secant steps from x0, x1 (measure grows with the angle)."""
        f0 = measure(x0)
        if target <= f0 + 0.5:
            return x0
        f1 = measure(x1)
        for _ in range(4):
            if abs(f1 - f0) < 1e-9:
                break
            x0, x1, f0 = x1, x1 + (target - f1) * (x1 - x0) / (f1 - f0), f1
            f1 = measure(x1)
            if abs(f1 - target) < 0.5:
                break
        return x1

    def carried(self) -> list[dict]:
        """The weights in the hands: palm link, centre (mm), grams, the handle's top."""
        if self.p["carry"] <= 0:
            return []
        grams = self.p["carry"] * 500
        height = grams / STEEL_DENSITY * 1000 / (math.pi * (WEIGHT_D / 2) ** 2)
        out = []
        for side, _ in SIDES:
            link = f"{side}_hand_palm_link"
            palm = pack().outlines[pack().manifest["links"][link]["pieces"][0]["key"]][0].mean(axis=0)
            grip = homogeneous(palm[None], self.poses[link])[0]
            centre = grip - (0.0, 0.0, HANDLE_L + height / 2)
            out.append({"side": side, "link": link, "grip": grip, "centre": centre, "grams": grams,
                        "height": height})
        return out

    def centre_of_mass(self) -> tuple[float, np.ndarray]:
        r = robot()
        centres = r.centres(self.poses)
        items = [(link.mass_g, centres[name]) for name, link in r.links.items()]
        items += [(w["grams"], w["centre"]) for w in self.weights]
        total = sum(m for m, _ in items)
        return total, sum(m * c for m, c in items) / total


def pose(p: dict) -> Pose:
    key = json.dumps(p, sort_keys=True)
    if key not in _poses:
        _poses.clear()
        _poses[key] = Pose(p)
    return _poses[key]


_poses: dict[str, Pose] = {}


# Build

def colour(tone: str, index: int) -> tuple[float, float, float]:
    """A link's first piece in Unitree's tone; its other pieces in contrast (dark on light, mid on dark)."""
    if index == 0:
        return DARK if tone == "dark" else LIGHT
    return MID if tone == "dark" else DARK


def build(p) -> BuildResult:
    r, mp = robot(), pack()
    s = pose(p)
    centres = r.centres(s.poses)
    links = mp.manifest["links"]
    masses: dict[str, FixedMass] = {}

    def parts(link: str) -> list:
        out = []
        for i, piece in enumerate(links.get(link, {}).get("pieces", ())):
            out.append(mp.place(piece["key"], s.poses[link], piece["key"], colour(links[link]["tone"], i)))
            masses[piece["key"]] = FixedMass(r.links[link].mass_g * piece["share"], tuple(centres[link]),
                                             "Aluminium / plastic")
        for w in s.weights:
            if w["link"] == link:
                out.append(weight(w))
                masses[out[-1].label] = FixedMass(w["grams"], tuple(w["centre"]), "Steel")
        return out

    def node(joint: rb.Joint):
        kids = parts(joint.child) + [k for k in (node(j) for j in r.children.get(joint.child, ())) if k is not None]
        return Compound(label=joint.name, children=kids) if kids else None   # sensor frames: nothing to show

    kids = parts(r.root) + [k for k in (node(j) for j in r.children.get(r.root, ())) if k is not None]
    return BuildResult(assembly=Compound(label="humanoid", children=kids), masses=masses)


def weight(w: dict):
    """A steel weight hanging from the palm on a short handle."""
    x, y, z = w["grip"]
    handle = Cylinder(HANDLE_D / 2, HANDLE_L + 2).moved(Location((x, y, z - HANDLE_L / 2)))
    body = Cylinder(WEIGHT_D / 2, w["height"]).moved(Location(tuple(w["centre"])))
    shape = handle + body
    shape.label, shape.color = f"{w['side']}_weight", Color(*STEEL)
    return shape


# Statics

def support(world_soles: dict[str, np.ndarray]) -> dict[str, np.ndarray]:
    """Each foot's sole points on the floor (within SOLE_TOL), for the feet that touch it."""
    floor = min(v[:, 2].min() for v in world_soles.values())
    return {side: v[v[:, 2] < floor + SOLE_TOL] for side, v in world_soles.items()
            if (v[:, 2] < floor + SOLE_TOL).any()}


def margin(points: np.ndarray, cg: np.ndarray) -> tuple[float, np.ndarray]:
    """Signed distance (mm) from the CG's ground point to the edge of the support polygon (the hull of the sole
    points): positive inside. And the polygon's centre."""
    hull = points[ConvexHull(points[:, :2]).vertices][:, :2] if len(points) >= 3 else points[:, :2]
    c = cg[:2]
    best, inside = math.inf, True
    for a, b in zip(hull, np.roll(hull, -1, axis=0)):
        ab, ac = b - a, c - a
        t = min(max(float(ab @ ac) / max(float(ab @ ab), 1e-9), 0.0), 1.0)
        best = min(best, float(np.linalg.norm(a + t * ab - c)))
        inside &= (ab[0] * ac[1] - ab[1] * ac[0]) >= 0   # hull vertices run counter-clockwise
    return (best if inside else -best), hull.mean(axis=0)


def reactions(feet: dict[str, np.ndarray], cg: np.ndarray, weight_n: float) -> list[rb.Load]:
    """The floor pushing up under the feet so the robot is in balance: split between two feet by the lever rule
    along the line between them, each push passing beside its foot's centre so the total acts under the CG."""
    centres = {side: pts[:, :2].mean(axis=0) for side, pts in feet.items()}
    if len(feet) == 1:
        side = next(iter(feet))
        return [rb.Load(f"{side}_ankle_roll_link", (cg[0], cg[1], 0.0), (0.0, 0.0, weight_n))]
    a, b = centres["left"], centres["right"]
    t = min(max(float((cg[:2] - a) @ (b - a)) / max(float((b - a) @ (b - a)), 1e-9), 0.0), 1.0)
    offset = cg[:2] - (a + t * (b - a))
    return [rb.Load(f"{side}_ankle_roll_link", (*(centres[side] + offset), 0.0), (0.0, 0.0, share * weight_n))
            for side, share in (("left", 1 - t), ("right", t))]


def joint_loads(poses: dict[str, np.ndarray], weights: list[dict], feet, cg: np.ndarray,
                total_g: float) -> dict[str, float]:
    """Each joint's static torque as a fraction of its rating."""
    loads = reactions(feet, cg, total_g / 1000 * G)
    loads += [rb.Load(w["link"], tuple(w["centre"]), (0.0, 0.0, -w["grams"] / 1000 * G)) for w in weights]
    torques = rb.static_torques(robot(), poses, loads)
    return {name: abs(t) / robot().joints[name].effort for name, t in torques.items() if robot().joints[name].effort}


def joint_words(name: str, loads: dict[str, float]) -> str:
    """'knees' when both sides work about as hard, else 'left knee'; 'wrists (pitch)' for the axis."""
    side, _, rest = name.partition("_")
    rest = rest.removesuffix("_joint")
    other = ("right_" if side == "left" else "left_") + rest + "_joint"
    if side not in ("left", "right"):
        part, _, axis = name.removesuffix("_joint").partition("_")
        return f"{part} ({axis})" if axis else part
    if rest.startswith("hand_"):
        return "fingers"
    part, _, axis = rest.partition("_")
    plural = {"hip": "hips", "knee": "knees", "ankle": "ankles", "shoulder": "shoulders", "elbow": "elbows",
              "wrist": "wrists"}.get(part, part + "s")
    both = other in loads and loads[other] > 0.9 * loads[name]
    words = plural if both else f"{side} {part}"
    return f"{words} ({axis})" if axis else words


def plural(words: str) -> bool:
    """'knees', 'wrists (pitch)' are plural; 'waist (pitch)', 'left knee' aren't."""
    part = words.split(" (")[0]
    return not part.startswith(("left ", "right ")) and part.endswith("s")


def limit_words(names: list[str]) -> str:
    """'The ankles (pitch, roll) are at their limits.' for joints held at their range's end, or ''."""
    axes: dict[str, list[str]] = {}
    for name in names:
        words = joint_words(name, {n: 1.0 for n in names})
        part, _, axis = words.partition(" (")
        axes.setdefault(part, []).extend([axis.rstrip(")")] if axis else [])
    if not axes:
        return ""
    phrases = [f"{part} ({', '.join(dict.fromkeys(a))})" if a else part for part, a in axes.items()]
    plural = len(phrases) > 1 or next(iter(axes)).endswith("s")
    return f" The {' and '.join(phrases)} {'are at their limits' if plural else 'is at its limit'}."


class Statics:
    """Balance and joint loads for a pose."""

    def __init__(self, s: Pose):
        world = {side: homogeneous(soles()[side], s.poses[f"{side}_ankle_roll_link"]) for side, _ in SIDES}
        self.feet = support(world)
        self.total_g, self.cg = s.centre_of_mass()
        self.margin, self.support_centre = margin(np.vstack(list(self.feet.values())), self.cg)
        self.loads = joint_loads(s.poses, s.weights, self.feet, self.cg, self.total_g)
        self.worst = max(self.loads, key=self.loads.get)
        self.peak = 100 * self.loads[self.worst]
        self.worst_words = joint_words(self.worst, self.loads)
        self.worst_rating = robot().joints[self.worst].effort
        self.worst_torque = self.loads[self.worst] * self.worst_rating
        away = self.cg[:2] - self.support_centre
        self.where = (("ahead of its toes" if away[1] < 0 else "behind its heels") if abs(away[1]) >= abs(away[0])
                      else f"out past its {'left' if away[0] > 0 else 'right'} foot")
        self.limits = limit_words([n for n in s.clamped if "hand" not in n])


# Analysis

def verdict(st: Statics) -> Verdict:
    if st.margin < 0:
        return Verdict("bad", "Falls over", f"Its centre of mass is {-st.margin:.0f} mm {st.where}, so it tips over."
                       + st.limits, "balance")
    many = plural(st.worst_words)
    if st.peak > 100:
        return Verdict("bad", "Motors overloaded", f"The {st.worst_words} {'need' if many else 'needs'} "
                                                   f"{st.worst_torque:.1f} N·m, rated {st.worst_rating:g}.", "joint_load")
    if st.margin < 25 or st.peak > 60:
        issue = (f"its centre of mass is only {st.margin:.0f} mm inside the edge of its feet" if st.margin < 25
                 else f"the {st.worst_words} {'work' if many else 'works'} at {st.peak:.0f}% of "
                      f"{'their' if many else 'its'} rating")
        return Verdict("warn", "Stands, barely", f"It holds, but {issue}.",
                       "balance" if st.margin < 25 else "joint_load")
    return Verdict("ok", "Stands steady", f"Centre of mass {st.margin:.0f} mm inside its feet; the hardest-working "
                                          f"{'joints' if many else 'joint'}, the {st.worst_words}, at {st.peak:.0f}% "
                                          "of rating.", "balance")


def analyze(result: BuildResult, masses) -> Analysis:
    p = result.params
    s = pose(p)
    st = Statics(s)
    bb = result.assembly.bounding_box()
    height = bb.max.Z
    metrics = [
        Metric("balance", "Balance margin", st.margin, "mm", band(st.margin, BALANCE_BANDS), -60.0, 120.0,
               BALANCE_BANDS, note="falls" if st.margin < 0 else "barely" if st.margin < 25 else "steady"),
        Metric("joint_load", "Peak joint load", st.peak, "%", band(st.peak, LOAD_BANDS), 0.0, 150.0, LOAD_BANDS,
               note=st.worst_words.split(" (")[0]),
        Metric("cg_height", "CG height", st.cg[2], "mm"),
        Metric("height", "Height", height, "mm"),
    ]
    walk = walk_summary(p, st)
    return Analysis(
        metrics=metrics,
        markers=[Marker("support", "Support", "support", (*st.support_centre, 0.0))],
        dimensions=dimensions(result, s),
        explode=explode(result, s),
        demo=walk,
        mass_groups=mass_groups(result),
        verdict=verdict(st),
        constants=[Constant("Robot", "Unitree G1, 29 joints", "URDF masses and ratings · BSD-3"),
                   Constant("Hands", "Dex3-1, 7 joints each", "3 fingers"),
                   Constant("Joint motors", "25–139 N·m peak", "shoulders 25 · waist 35 · knees 139")],
        extra={"assumptions": ASSUMPTIONS + ([f"Held at its limit: {', '.join(s.clamped)}."] if s.clamped else [])})


def dimensions(result: BuildResult, s: Pose) -> list[Dimension]:
    bb = result.assembly.bounding_box()
    x0, x1, y0 = bb.min.X, bb.max.X, bb.min.Y
    feet = {side: homogeneous(soles()[side], s.poses[f"{side}_ankle_roll_link"]).mean(axis=0) for side, _ in SIDES}
    shoulders = {side: s.poses[f"{side}_shoulder_roll_link"][:3, 3] for side, _ in SIDES}
    hip = s.poses["left_hip_pitch_link"][:3, 3]
    top = max(shoulders["left"][2], shoulders["right"][2]) + 40
    return [
        Dimension("height", "Height", (x1, y0, 0.0), (x1, y0, bb.max.Z), (1.0, 0.0, 0.0), 70.0),
        Dimension("shoulders", "Shoulder width", (shoulders["right"][0], shoulders["right"][1], top),
                  (shoulders["left"][0], shoulders["left"][1], top), (0.0, 0.0, 1.0), bb.max.Z - top + 60),
        Dimension("leg", "Leg length", (x0, hip[1], 0.0), (x0, hip[1], hip[2]), (-1.0, 0.0, 0.0), 70.0),
        Dimension("stance", "Stance width", (feet["right"][0], y0, 0.0), (feet["left"][0], y0, 0.0),
                  (0.0, -1.0, 0.0), 60.0),
    ]


def depth(link: str) -> int:
    """How far down its limb a link is (1 = next to the body)."""
    chain = robot().chain(link)
    return sum(1 for j in chain if not j.startswith(("waist", "pelvis"))) or 1


def explode(result: BuildResult, s: Pose) -> Explode:
    """Limbs come off along their chains, each link a step further than the one above it: legs down and out, arms
    and hands out to the sides, the head up, the pelvis cover forward. A link's other pieces lift off its main
    piece. The pelvis, waist and torso stay (the torso's other pieces lift off it)."""
    moves = []
    for link, e in pack().manifest["links"].items():
        side = 1.0 if link.startswith("left") else -1.0
        if link.endswith(("hip_pitch_link", "hip_roll_link", "hip_yaw_link", "knee_link", "ankle_pitch_link",
                          "ankle_roll_link")):
            way, step = (0.45 * side, 0.0, -1.0), 60.0
        elif link.startswith(("left", "right")):
            way, step = (side, 0.0, 0.12), 48.0
        elif link == "head_link":
            way, step = (0.0, 0.0, 1.0), 130.0
        elif link == "pelvis_contour_link":
            way, step = (0.0, -1.0, 0.0), 90.0
        else:
            way, step = (0.0, 0.0, 0.0), 0.0
        d = depth(link)
        along = np.array(way) / (np.linalg.norm(way) or 1.0) * step * d
        centres = [homogeneous(pack().outlines[p["key"]][0], s.poses[link]).mean(axis=0) for p in e["pieces"]]
        for i, piece in enumerate(e["pieces"]):
            offset = along.copy()
            if i:
                apart = centres[i] - centres[0]
                offset += apart / (np.linalg.norm(apart) or 1.0) * 45.0
            dist = float(np.linalg.norm(offset))
            if dist > 0.5:
                moves.append(ExplodeMove(piece["key"], tuple(offset), dist, order=min(3, d - 1)))
    for w in s.weights:
        moves.append(ExplodeMove(f"{w['side']}_weight", (0.0, 0.0, -1.0), 160.0, order=3))
    return Explode("radial", moves)


def mass_groups(result: BuildResult) -> dict[str, list[str]]:
    groups = {"Legs": [], "Arms": [], "Hands": [], "Torso": [], "Head and pelvis": [], "Load": []}
    for shape in result.assembly.leaves:
        name = shape.label
        if "weight" in name:
            groups["Load"].append(name)
        elif any(k in name for k in ("hip_", "thigh", "shin", "ankle", "foot")):
            groups["Legs"].append(name)
        elif any(k in name for k in ("palm", "thumb", "index", "middle")):
            groups["Hands"].append(name)
        elif any(k in name for k in ("shoulder", "upper_arm", "forearm", "wrist")):
            groups["Arms"].append(name)
        elif any(k in name for k in ("torso", "waist")):
            groups["Torso"].append(name)
        else:
            groups["Head and pelvis"].append(name)
    return {k: v for k, v in groups.items() if v}


# Walk

@lru_cache(maxsize=1)
def clip() -> dict:
    return json.loads((DATA / "walk_clip.json").read_text(encoding="utf-8"))


def quat_matrix(w, x, y, z) -> np.ndarray:
    return np.array([[1 - 2 * (y * y + z * z), 2 * (x * y - w * z), 2 * (x * z + w * y)],
                     [2 * (x * y + w * z), 1 - 2 * (x * x + z * z), 2 * (y * z - w * x)],
                     [2 * (x * z - w * y), 2 * (y * z + w * x), 1 - 2 * (x * x + y * y)]])


def matrix_quat(m: np.ndarray) -> tuple[float, float, float, float]:
    """(x, y, z, w) of a rotation matrix."""
    w = math.sqrt(max(0.0, 1 + m[0, 0] + m[1, 1] + m[2, 2])) / 2
    x = math.copysign(math.sqrt(max(0.0, 1 + m[0, 0] - m[1, 1] - m[2, 2])) / 2, m[2, 1] - m[1, 2])
    y = math.copysign(math.sqrt(max(0.0, 1 - m[0, 0] + m[1, 1] - m[2, 2])) / 2, m[0, 2] - m[2, 0])
    z = math.copysign(math.sqrt(max(0.0, 1 - m[0, 0] - m[1, 1] + m[2, 2])) / 2, m[1, 0] - m[0, 1])
    return x, y, z, w


class Walk:
    """The recorded lap's frames on this version: legs from the clip, arms swinging opposite the legs, everything
    else as posed. The pelvis follows the clip's path, turned so the robot sets off facing -Y from where it stands,
    and raised or lowered a hair each frame so the lower foot touches the floor."""

    def __init__(self, p: dict):
        c, s = clip(), pose(p)
        self.version = s
        self.rate_hz = c["rate_hz"]
        names = c["joints"]
        angles = np.array(c["angles"])
        positions = np.array(c["pelvis_pos"]) * 1000
        hips = {side: angles[:, names.index(f"{side}_hip_pitch_joint")] for side, _ in SIDES}
        start = quat_matrix(*c["pelvis_quat"][0])
        turn = rb.axis_angle((0, 0, 1), -math.pi / 2 - math.atan2(start[1, 0], start[0, 0]))   # set off facing -Y
        inverse = np.linalg.inv(s.base)
        self.angles, self.models = [], []
        for i, q in enumerate(c["pelvis_quat"]):
            a = dict(s.angles)
            a.update(zip(names, angles[i].tolist()))
            for side, _ in SIDES:
                a[f"{side}_shoulder_pitch_joint"] = s.angles[f"{side}_shoulder_pitch_joint"] - \
                    ARM_SWING * (hips[side][i] - hips[side].mean())
            pelvis = rb.transform(turn @ quat_matrix(*q), turn @ (positions[i] - positions[0]))
            pelvis[:2, 3] += s.base[:2, 3]
            poses = robot().poses(a, pelvis)
            pelvis[2, 3] -= min(homogeneous(soles()[side], poses[f"{side}_ankle_roll_link"])[:, 2].min()
                                for side, _ in SIDES)
            self.angles.append(a)
            self.models.append(pelvis @ inverse)
        self.centre = np.array([m[:2, 3] for m in self.models]).mean(axis=0)


class WalkLoad:
    """How hard the joints work over the lap: the legs' torques from the simulation (the bare robot), scaled by
    this version's weight with its load; the upper body's as it's posed (static). Per frame and the peak."""

    def __init__(self, st: Statics):
        c = clip()
        names = c["joints"]
        scale = st.total_g / 1000 / c["model_mass_kg"]
        ratings = np.array([robot().joints[n].effort for n in names])
        legs = np.abs(np.array(c["torques"])) * scale / ratings          # frames x 12, share of rating
        upper = {n: v for n, v in st.loads.items() if n not in names}
        upper_name = max(upper, key=upper.get)
        self.track = (np.maximum(legs.max(axis=1), upper[upper_name]) * 100).tolist()
        peaks = {**upper, **dict(zip(names, legs.max(axis=0).tolist()))}
        name = max(peaks, key=peaks.get)
        self.peak, self.words = 100 * peaks[name], joint_words(name, peaks)
        self.rating = robot().joints[name].effort
        self.torque = peaks[name] * self.rating


def walk_blocked(st: Statics, load: WalkLoad) -> str | None:
    if st.margin < 0:
        return "It falls over standing still: fix its balance first."
    if load.peak > 100:
        return f"Too much to walk with: the {load.words} would need {load.torque:.1f} N·m, rated {load.rating:g}."
    return None


def walk_summary(p: dict, st: Statics) -> Demo:
    lap = clip()["lap"]
    load = WalkLoad(st)
    return Demo(
        kind="walk", label="Walk", source="/cad/motion",
        note=f"Walks a {lap['radius_m']:.2f} m circle with Unitree's own trained gait, recorded in MuJoCo.",
        readouts=[Metric("speed", "Speed", lap["mean_speed_m_s"], "m/s", digits=2),
                  Metric("steps", "Step rate", lap["step_rate_hz"] * 60, "steps/min"),
                  Metric("walk_load", "Peak joint load", load.peak, "%", band(load.peak, LOAD_BANDS), 0.0, 150.0,
                         LOAD_BANDS, note=load.words.split(" (")[0],
                         display=f"{load.peak:.0f}% · {load.words.split(' (')[0].split()[-1]}")],
        values={"speed_m_s": lap["mean_speed_m_s"], "step_rate_hz": lap["step_rate_hz"], "lap_s": lap["duration_s"],
                "radius_mm": lap["radius_m"] * 1000, "peak_load_pct": load.peak},
        assumptions=["Legs from Unitree's walking policy (unitree_rl_gym), recorded once in MuJoCo; the arms "
                     "swing opposite the legs; the rest holds this version's pose.",
                     f"Leg torques from that simulation ({clip()['model_mass_kg']:.1f} kg robot), scaled by this "
                     "version's weight with its load; upper-body loads static, as posed."],
        blocked=walk_blocked(st, load))


def simulate_motion(result: BuildResult, masses) -> Motion:
    """The Walk demo's frames (GET /cad/motion): every leg joint and both shoulders, and the whole robot's path."""
    p = result.params
    w = Walk(p)
    s = w.version
    lap = clip()["lap"]
    joints = [f"{side}_{j}_joint" for side, _ in SIDES for j in ("hip_pitch", "hip_roll", "hip_yaw", "knee",
                                                                    "ankle_pitch", "ankle_roll", "shoulder_pitch")]
    out = []
    for name in joints:
        origin, axis = robot().axis(name, s.poses)
        rest = s.angles.get(name, 0.0)
        out.append(MotionJoint(name, tuple(origin), tuple(axis),
                               tuple(math.degrees(a.get(name, 0.0) - rest) for a in w.angles)))
    rotations, last = [], None
    for m in w.models:
        q = np.array(matrix_quat(m[:3, :3]))
        if last is not None and q @ last < 0:
            q = -q   # keep the sign continuous
        rotations.append(tuple(q.tolist()))
        last = q
    return Motion(rate_hz=w.rate_hz, joints=out, positions=[tuple(m[:3, 3]) for m in w.models], rotations=rotations,
                  loop=True, centre=(float(w.centre[0]), float(w.centre[1]), 0.0),
                  values={"radius_mm": lap["radius_m"] * 1000, "lap_s": lap["duration_s"],
                          "speed_m_s": lap["mean_speed_m_s"], "step_rate_hz": lap["step_rate_hz"]},
                  tracks={"load_pct": WalkLoad(Statics(s)).track})
