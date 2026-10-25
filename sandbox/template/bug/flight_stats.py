"""Numbers for a launch: apogee, time to apogee, average climb rate. Used by bug/test_flight_stats.py.

Planted bug for the demo ("Albie, the flight stats test fails, fix it"): average_climb_rate is off.
"""


def apogee(altitudes_m: list[float]) -> float:
    """Highest altitude reached."""
    return max(altitudes_m)


def time_to_apogee(times_s: list[float], altitudes_m: list[float]) -> float:
    """Seconds from launch to the highest point."""
    return times_s[altitudes_m.index(apogee(altitudes_m))]


def average_climb_rate(times_s: list[float], altitudes_m: list[float]) -> float:
    """Average vertical speed (m/s) from launch to apogee."""
    peak = altitudes_m.index(apogee(altitudes_m))
    return (altitudes_m[peak] - altitudes_m[0]) / (times_s[peak] - times_s[1])
