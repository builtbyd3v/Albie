"""A robot from its URDF: links with masses, joints with axes, limits and torque ratings, forward kinematics, and the
static joint torques a pose needs. Part millimetres, Z up, like every part script; angles in radians.

Loaded once per worker process (`load`, cached), like cad/meshes.py, for the same reason: part scripts are imported
afresh on every build.
"""
from __future__ import annotations

import math
import xml.etree.ElementTree as ET
from dataclasses import dataclass, field
from functools import cached_property, lru_cache
from pathlib import Path

import numpy as np

G = 9.81


def rpy_matrix(rpy) -> np.ndarray:
    """URDF roll, pitch, yaw (fixed axes X, Y, Z) as a rotation matrix."""
    r, p, y = rpy
    cr, sr, cp, sp, cy, sy = math.cos(r), math.sin(r), math.cos(p), math.sin(p), math.cos(y), math.sin(y)
    return np.array([[cy * cp, cy * sp * sr - sy * cr, cy * sp * cr + sy * sr],
                     [sy * cp, sy * sp * sr + cy * cr, sy * sp * cr - cy * sr],
                     [-sp, cp * sr, cp * cr]])


def axis_angle(axis, angle: float) -> np.ndarray:
    a = np.asarray(axis, float) / np.linalg.norm(axis)
    k = np.array([[0, -a[2], a[1]], [a[2], 0, -a[0]], [-a[1], a[0], 0]])
    return np.eye(3) + math.sin(angle) * k + (1 - math.cos(angle)) * (k @ k)


def transform(rotation=None, translation=None) -> np.ndarray:
    t = np.eye(4)
    if rotation is not None:
        t[:3, :3] = rotation
    if translation is not None:
        t[:3, 3] = translation
    return t


@dataclass(frozen=True)
class Link:
    name: str
    mass_g: float
    com: np.ndarray           # centre of mass in the link's frame (mm)


@dataclass(frozen=True)
class Joint:
    name: str
    kind: str                 # revolute | continuous | prismatic | fixed
    parent: str
    child: str
    origin: np.ndarray        # 4x4: parent link frame -> joint frame (mm)
    axis: np.ndarray          # unit, in the joint frame
    lower: float = 0.0        # rad
    upper: float = 0.0
    effort: float = 0.0       # rated torque, N·m

    @property
    def moves(self) -> bool:
        return self.kind in ("revolute", "continuous")

    def clamp(self, q: float) -> float:
        return min(max(q, self.lower), self.upper) if self.kind == "revolute" else q


@dataclass
class Robot:
    links: dict[str, Link]
    joints: dict[str, Joint]
    root: str
    children: dict[str, list[Joint]] = field(default_factory=dict)   # link -> the joints hanging off it
    above: dict[str, Joint] = field(default_factory=dict)            # link -> the joint it hangs from

    @property
    def mass_g(self) -> float:
        return sum(link.mass_g for link in self.links.values())

    def clamp(self, angles: dict[str, float]) -> tuple[dict[str, float], list[str]]:
        """Angles inside each joint's limits, and the names that had to move."""
        out, clamped = {}, []
        for name, q in angles.items():
            out[name] = self.joints[name].clamp(q)
            if abs(out[name] - q) > 1e-9:
                clamped.append(name)
        return out, clamped

    def poses(self, angles: dict[str, float], base: np.ndarray | None = None) -> dict[str, np.ndarray]:
        """Each link's frame (4x4, part mm) for these joint angles (missing = 0), the root link at `base`."""
        out = {self.root: np.eye(4) if base is None else np.array(base, float)}
        stack = [self.root]
        while stack:
            link = stack.pop()
            for j in self.children.get(link, ()):
                q = angles.get(j.name, 0.0) if j.moves else 0.0
                out[j.child] = out[link] @ j.origin @ transform(axis_angle(j.axis, q))
                stack.append(j.child)
        return out

    def axis(self, joint: str, poses: dict[str, np.ndarray]) -> tuple[np.ndarray, np.ndarray]:
        """A joint's pivot point and unit axis in part space for these link poses."""
        j = self.joints[joint]
        frame = poses[j.parent] @ j.origin
        return frame[:3, 3].copy(), frame[:3, :3] @ (j.axis / np.linalg.norm(j.axis))

    def below(self, link: str) -> list[str]:
        """The link and every link hanging from it."""
        out, stack = [], [link]
        while stack:
            name = stack.pop()
            out.append(name)
            stack.extend(j.child for j in self.children.get(name, ()))
        return out

    def chain(self, link: str) -> list[str]:
        """The joints from the root down to `link`, root first."""
        out = []
        while link in self.above:
            out.append(self.above[link].name)
            link = self.above[link].parent
        return out[::-1]

    def centres(self, poses: dict[str, np.ndarray]) -> dict[str, np.ndarray]:
        """Each link's centre of mass in part space."""
        return {name: (poses[name] @ np.append(link.com, 1.0))[:3] for name, link in self.links.items()}

    @cached_property
    def carried(self) -> tuple[list[str], list[str], np.ndarray]:
        """(moving joints, links, J x L matrix): 1 where the link hangs below the joint, so the joint carries it."""
        joints = [name for name, j in self.joints.items() if j.moves]
        links = list(self.links)
        index = {name: i for i, name in enumerate(links)}
        mask = np.zeros((len(joints), len(links)))
        for row, name in enumerate(joints):
            mask[row, [index[link] for link in self.below(self.joints[name].child)]] = 1.0
        return joints, links, mask


@dataclass(frozen=True)
class Load:
    """A force on the robot (part mm, newtons): gravity on a carried mass, or a ground reaction under a foot.
    `link`: the link it acts on, so a joint knows whether it carries it."""
    link: str
    point: tuple[float, float, float]
    force: tuple[float, float, float]


def static_torques(robot: Robot, poses: dict[str, np.ndarray], loads: list[Load] = ()) -> dict[str, float]:
    """The torque each moving joint must hold (N·m, signed about its axis) to keep this pose still: gravity on every
    link below it, plus the loads acting below it. The robot must be in balance overall (the ground reactions in
    `loads` carry its weight), so it doesn't matter which end is fixed."""
    joints, links, mask = robot.carried
    index = {name: i for i, name in enumerate(links)}
    centres = robot.centres(poses)
    points = np.array([centres[name] for name in links]) / 1000
    forces = np.zeros((len(links), 3))
    forces[:, 2] = [-robot.links[name].mass_g / 1000 * G for name in links]
    moments = np.cross(points, forces)
    for load in loads:   # each load joins its link's sums
        i = index[load.link]
        moments[i] += np.cross(np.asarray(load.point) / 1000, load.force)
        forces[i] += load.force
    pivots, axes = zip(*(robot.axis(name, poses) for name in joints))
    pivots, axes = np.array(pivots) / 1000, np.array(axes)
    moment = mask @ moments - np.cross(pivots, mask @ forces)   # about each pivot
    return dict(zip(joints, (-(moment * axes).sum(axis=1)).tolist()))   # what the motor supplies to cancel it


@lru_cache(maxsize=None)
def load(urdf: Path) -> Robot:
    """Parse a URDF (cached per path): metres -> mm, links without inertia weigh nothing."""
    xml = ET.parse(urdf).getroot()

    def vec(text: str | None, default="0 0 0") -> np.ndarray:
        return np.array([float(v) for v in (text or default).split()])

    links = {}
    for el in xml.findall("link"):
        inertial = el.find("inertial")
        mass = float(inertial.find("mass").get("value")) * 1000 if inertial is not None else 0.0
        origin = inertial.find("origin") if inertial is not None else None
        com = vec(origin.get("xyz")) * 1000 if origin is not None else np.zeros(3)
        links[el.get("name")] = Link(el.get("name"), mass, com)
    joints = {}
    for el in xml.findall("joint"):
        origin, limit, axis = el.find("origin"), el.find("limit"), el.find("axis")
        frame = transform(rpy_matrix(vec(origin.get("rpy") if origin is not None else None)),
                          vec(origin.get("xyz") if origin is not None else None) * 1000)
        j = Joint(el.get("name"), el.get("type"), el.find("parent").get("link"), el.find("child").get("link"),
                  frame, vec(axis.get("xyz") if axis is not None else None, "1 0 0"),
                  float(limit.get("lower", 0)) if limit is not None else 0.0,
                  float(limit.get("upper", 0)) if limit is not None else 0.0,
                  float(limit.get("effort", 0)) if limit is not None else 0.0)
        joints[j.name] = j
    children: dict[str, list[Joint]] = {}
    for j in joints.values():
        children.setdefault(j.parent, []).append(j)
    root = next(name for name in links if name not in {j.child for j in joints.values()})
    return Robot(links, joints, root, children, {j.child: j for j in joints.values()})
