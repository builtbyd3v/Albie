using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

/// <summary>
/// The design's demo (analysis.demo), per CAD UX spec section 4. World-locked; you follow the model with your head.
/// - launch (Fly, about 15.5 s): a 1.2 s countdown on a pad of light (the ring pulses three times, a rail 5 deg off
///   vertical, the sim's predicted path faintly dashed with "Apogee 197 m · predicted" at its top); then the flight
///   sim's samples in real time with apogee <see cref="launchHeight"/> above the desk (kept 0.3 m under the ceiling),
///   downrange away from you; the nose follows the path (locked to the rail until rail exit), a flame while the motor
///   burns, a glowing trail, tags at burnout, apogee and the parachute; under the canopy it turns nose-up and the descent
///   plays ×8 with a dashed trail; it holds 1.4 s on landing ("Landed 50.3 s · 36 m from the pad"), then glides home.
///   A chip beside the rocket reads the sim's altitude, speed and time.
/// - hover (Hover, 9.8 s): props spool up and blur into glowing discs, it lifts 0.22 m (ease-out), hovers with an 8 mm bob
///   and a 4 deg yaw sway, thrust columns to scale on each motor (dashed to max thrust, bright to the hover share), a
///   height line, readouts; lands and spools down.
/// - spin (Run, 10.4 s): the lid (demo.reveal_ids, else what explodes furthest up) lifts and turns to glass, every
///   spinner turns at rpm ÷ <see cref="spinSlowdown"/> about its own axis, so the ratio shows; dashed pitch circles,
///   rotation arrows round the shaft ends, In / Out chips; it stops and the lid comes back.
/// - walk (Walk, about 13 s): the keyframed motion from GET demo.source (/cad/motion, the humanoid's lap of Unitree's
///   trained gait): it steps out to the lap, centred on its base (<see cref="walkAroundBase"/>), walks it once with
///   every joint turning about its pivot and the whole model following the path (feet on the base), then steps back.
///   A dashed lap circle, and a chip with the speed and how hard its hardest-working joint works now.
/// Every demo ends with the model exactly as it was. CadViewer owns one, binds it to each shown version, ticks it in
/// LateUpdate (after the turntable), applies its lid reveal (<see cref="RevealLift"/>, <see cref="RevealGlass"/>), and
/// stops it at once when the version, the view or the place changes.
/// </summary>
public sealed class CadDemoPlayer
{
    public const string Launch = "launch", Hover = "hover", Spin = "spin", Walk = "walk";

    // Tuning: CadViewer copies its fields in before each start.
    public float launchHeight = 1.3f, descentSpeedup = 8f, maxDrift = 0.35f;
    public float hoverHeight = 0.22f, hoverSeconds = 6f, propTurnsPerSecond = 4.5f;
    public float spinSlowdown = 100f, spinSeconds = 7.6f;
    /// <summary>Walk: centre the lap on the model's base (it steps out to it) rather than set off from where it stands.</summary>
    public bool walkAroundBase = true;
    /// <summary>Set while something else moves the model (CadViewer's trip to or from the stage): a start waits for it.</summary>
    public bool hold;

    const float Countdown = 1.2f, LandedHold = 1.4f, HomeSeconds = 0.6f, ChuteOpen = 0.35f;
    const float SpinUp = 0.8f, TakeOff = 1.2f, LandSeconds = 1.2f, SpinDown = 0.6f;
    const float LidUp = 0.6f, Ramp = 0.8f, Stopping = 0.8f, LidDown = 0.6f;
    const float StepOut = 0.9f, StepBack = 0.9f;
    const float PrepTimeout = 75f;          // the hub's flight sim gets 60 s
    const int PathMax = 420;
    static readonly CadDemo LegacyLaunch = new CadDemo { kind = Launch, label = "Fly", source = "/cad/flight" };
    static readonly Color Warm = new Color(1f, 0.82f, 0.66f);

    enum Stage { Idle, Prep, Run, End }

    struct Spinner
    {
        public string id;
        public Transform transform;
        public Vector3 restPosition;
        public Quaternion restRotation;
        public Vector3 origin, axis;              // in the part's parent space
        public Vector3 originHolder, axisHolder;  // holder space
        public Vector3 centreHolder;              // the part's centre on its axis (holder space): a gear's plane
        public float ratio;                       // its angle per degree of the reference (signed, AngleAxis sense)
        public float radius;                      // how far it reaches from its axis (holder units)
        public float pitch;                       // gear pitch radius, if known (holder units), else 0
    }

    struct WalkJoint
    {
        public Transform transform;
        public Vector3 restPosition;
        public Quaternion restRotation;
        public Vector3 origin, axis;              // in its parent's space at rest (the parent turns with it)
        public float[] angles;                    // degrees per sample, hub sense (right-hand rule in glTF)
    }

    /// <summary>Started (true: getting ready or running) or ended (false).</summary>
    public event Action<bool> Changed;
    /// <summary>Every frame while it runs.</summary>
    public event Action<CadDemoFrame> Frame;

    public bool Running => stage != Stage.Idle;
    /// <summary>Fly, Hover and Walk move the whole model: markers and labels step aside while they run.</summary>
    public bool MovesModel => stage != Stage.Idle && (kind == Launch || kind == Hover || kind == Walk);
    public string Kind => stage != Stage.Idle ? kind : null;
    /// <summary>Run: how far the lid is lifted, as a share of its explode distance (CadViewer applies it).</summary>
    public float RevealLift { get; private set; }
    /// <summary>Run: how much the lid has turned to glass (0 solid, 0.45 at the top).</summary>
    public float RevealGlass { get; private set; }

    readonly CadExploder exploder;
    readonly CadLines lines, marks;
    readonly CadChips chips;
    readonly CadLabel readout, tag1, tag2, tag3, chipIn, chipOut, slowTag;
    CadSession session;

    // The version on show (Bind).
    Transform holder;
    CadModel model;
    CadModelUpdated info;
    Bounds assembled;

    Stage stage = Stage.Idle;
    string kind;
    CadDemo demo;
    int token;
    float clock, prepClock, endStart;
    bool partsWereOff;
    CadDemoFrame frame;
    string formatKey;    // what the readout's text was built for (landed, the hover title)
    string formatPhase;  // in flight: the phase the readout's format string was built for...
    bool formatFast;     // ...and whether the descent was sped up
    bool simulatingShown;

    // The holder's rest pose in its parent, and the model's axes (holder space).
    Transform restParent;
    Vector3 restPosition, restScale;
    Quaternion restRotation;
    Vector3 pivot, axisOrigin, axisDir;
    float tipAlong;  // from axisOrigin along axisDir to the far end (the nose)

    // Launch.
    CadFlight flight;
    bool flightFailed, deploys, apexReached;
    List<CadFlightSample> samples;
    float tApogee, tDeploy, tLand, tBurnout, tRail, tSwitch, vScale, hScale, landedAt;
    Vector3 pad, padPivot, downDir, railDir, travel, apex, burnoutPoint, deployPoint, landingPoint, endFromPosition;
    Quaternion flightRotation, endFromRotation;
    readonly Vector3[] path = new Vector3[PathMax];
    readonly float[] pathT = new float[PathMax];
    int pathCount, apexIndex;
    string countdownSub, landedTitle, landedSub;

    // Hover and spin.
    readonly List<Spinner> spinners = new List<Spinner>();
    float referenceSpeed;     // deg/s of the fastest spinner on screen
    float phi, speed;         // the reference's angle (deg) and the share of full speed
    int symmetry;             // turns of the reference after which every spinner looks as it started (0: none)
    bool stopping;
    float stopStart, stopSeconds, stopFrom, stopDelta, stopSpeed, stopShare;
    float height, endFromHeight, thrustShare, lid, runEnd;
    string hoverSub, hoverTitle;

    // Walk.
    CadMotion motion;
    bool motionFailed;
    readonly List<WalkJoint> walkJoints = new List<WalkJoint>();
    Vector3[] walkPos;        // the model's place per sample (holder space), the lap centred if walkAroundBase
    Quaternion[] walkRot;
    float[] walkLoad;         // % of rating per sample, or null
    int walkCount;
    float walkRate, lapSeconds, lapRadius, walkSpeed, walkShare, lapTime, endShare, endLap;
    string walkTitle, walkSub;

    public CadDemoPlayer(Transform overlayRoot, CadExploder exploder)
    {
        this.exploder = exploder;
        lines = new CadLines("CadDemo", overlayRoot, CadLook.Light(overlay: false));
        marks = new CadLines("CadDemoMarks", overlayRoot, CadLook.Light(overlay: true));
        chips = new CadChips("CadDemoChips", overlayRoot);
        readout = new CadLabel(overlayRoot, "CadDemoReadout", CadLabel.Style.Read);
        tag1 = new CadLabel(overlayRoot, "CadDemoTag1", CadLabel.Style.Event);
        tag2 = new CadLabel(overlayRoot, "CadDemoTag2", CadLabel.Style.Event);
        tag3 = new CadLabel(overlayRoot, "CadDemoTag3", CadLabel.Style.Event);
        chipIn = new CadLabel(overlayRoot, "CadDemoIn", CadLabel.Style.Read);
        chipOut = new CadLabel(overlayRoot, "CadDemoOut", CadLabel.Style.Read);
        slowTag = new CadLabel(overlayRoot, "CadDemoSlow", CadLabel.Style.Tag);
    }

    /// <summary>
    /// The version's demo: analysis.demo, or for versions made before it existed, Fly for designs with a flight sim.
    /// Null when there's nothing to run (kind none, or a kind this viewer doesn't know).
    /// </summary>
    public static CadDemo Resolve(CadAnalysis analysis, CadParams parameters)
    {
        var d = analysis?.demo;
        if (d != null) return d.kind == Launch || d.kind == Hover || d.kind == Spin || d.kind == Walk ? d : null;
        return parameters != null && parameters.Has("flight") ? LegacyLaunch : null;
    }

    /// <summary>
    /// The parts Run lifts and turns to glass, and the stage shows through: demo.reveal_ids, else (a Run design) the part
    /// that explodes furthest straight up among those that don't turn (the gearbox's lid). Null if none.
    /// </summary>
    public static List<string> RevealIds(CadAnalysis analysis)
    {
        var d = analysis?.demo;
        if (d == null) return null;
        if (d.revealIds != null && d.revealIds.Count > 0) return d.revealIds;
        if (d.kind != Spin || analysis.explode?.parts == null) return null;
        var turning = new HashSet<string>();
        if (d.spinParts != null)
            foreach (var s in d.spinParts)
                if (s != null && s.id != null) turning.Add(s.id);
        CadExplodePart best = null;
        foreach (var e in analysis.explode.parts)
            if (e != null && e.id != null && !turning.Contains(e.id) && e.distMm > 0f && e.dir != null && e.dir.Length >= 3 &&
                e.dir[1] > 0.9f && (best == null || e.distMm > best.distMm))
                best = e;
        return best != null ? new List<string> { best.id } : null;
    }

    /// <summary>The version on show (CadViewer, after each swap). Stops a running demo first.</summary>
    public void Bind(Transform holderTransform, CadModel shownModel, CadModelUpdated shownInfo, Bounds assembledBounds)
    {
        Stop(instant: true);
        holder = holderTransform;
        model = shownModel;
        info = shownInfo;
        assembled = assembledBounds;
    }

    /// <summary>
    /// Start the demo. It first reassembles the model (explode off, parts taken off put back) and, for Fly, gets the
    /// flight. False if this version has none, or the hub says it can't run (demo.blocked).
    /// </summary>
    public bool Start(CadSession cadSession)
    {
        if (stage != Stage.Idle) return true;
        if (cadSession == null || holder == null || model == null || model.Root == null) return false;
        var d = Resolve(info?.analysis, cadSession.Params);
        if (d == null || !string.IsNullOrEmpty(d.blocked)) return false;
        session = cadSession;
        demo = d;
        kind = d.kind;
        token++;
        flight = null;
        flightFailed = false;
        motion = null;
        motionFailed = false;
        stage = Stage.Prep;
        prepClock = 0f;
        simulatingShown = false;
        partsWereOff = session.PartsOff > 0;
        if (session.View.explode == true) session.SetView(false, session.View.focusId);
        if (partsWereOff) session.Reassemble();
        Changed?.Invoke(true);
        if (kind == Launch) FetchFlight();
        else if (kind == Walk) FetchMotion();
        return true;
    }

    /// <summary>Stop. <paramref name="instant"/>: the model is back as it was now; otherwise it lands, glides home or spins down first.</summary>
    public void Stop(bool instant)
    {
        if (stage == Stage.Idle) return;
        if (instant || stage == Stage.Prep)
        {
            Finish();
            return;
        }
        if (stage != Stage.End) BeginEnd();
    }

    /// <summary>CadViewer.LateUpdate: after the turntable and the grips have moved the model's parents this frame.</summary>
    public void Tick(float dt)
    {
        if (stage == Stage.Idle) return;
        if (holder == null || model == null || model.Root == null)
        {
            Finish();
            return;
        }
        var cam = Camera.main;
        Vector3 eye = cam != null ? cam.transform.position : holder.position - Vector3.forward;
        if (stage == Stage.Prep)
        {
            TickPrep(dt, eye);
            return;
        }
        clock += dt;
        lines.Begin(eye);
        marks.Begin(eye);
        chips.Begin();
        if (kind == Launch) TickLaunch(dt, eye);
        else if (kind == Hover) TickHover(dt, eye);
        else if (kind == Walk) TickWalk(dt, eye);
        else TickSpin(dt, eye);
        if (stage == Stage.Idle) return;  // it finished this frame
        DrawChips();
        lines.End();
        marks.End();
        chips.End();
        Frame?.Invoke(frame);
    }

    public void Dispose()
    {
        Stop(instant: true);
        lines.Destroy();
        marks.Destroy();
        chips.Destroy();
        readout.Destroy();
        tag1.Destroy();
        tag2.Destroy();
        tag3.Destroy();
        chipIn.Destroy();
        chipOut.Destroy();
        slowTag.Destroy();
    }

    // ---- Shared ----

    void FetchFlight()
    {
        int mine = token;
        session.GetFlight(info.version, demo.source, f =>
        {
            if (mine != token || stage != Stage.Prep) return;  // stopped (or started again) meanwhile
            if (f == null) flightFailed = true;
            else flight = f;
        });
    }

    void FetchMotion()
    {
        int mine = token;
        session.GetMotion(info.version, demo.source, m =>
        {
            if (mine != token || stage != Stage.Prep) return;
            if (m == null || m.joints == null || m.joints.Count == 0) motionFailed = true;
            else motion = m;
        });
    }

    void TickPrep(float dt, Vector3 eye)
    {
        prepClock += dt;
        if (flightFailed || motionFailed || prepClock > PrepTimeout)
        {
            Finish();
            return;
        }
        bool together = exploder == null || (!exploder.Moving && exploder.Progress < 0.002f);
        bool ready = together && !hold && (!partsWereOff || prepClock > 0.45f) && (kind != Launch || flight != null) &&
                     (kind != Walk || motion != null);
        if (ready)
        {
            BeginRun(eye);
            return;
        }
        if ((kind == Launch && flight == null || kind == Walk && motion == null) && prepClock > 0.25f)
        {
            if (!simulatingShown)
            {
                simulatingShown = true;  // set once: SetText builds its rich text, so not every frame of the wait
                readout.SetText(kind == Walk ? "Getting the walk…" : "Simulating the flight…");
                readout.SetAnchor(1);
            }
            readout.MoveTo(holder.TransformPoint(assembled.center) + Right(eye, holder.position) * (HalfWidth() + 0.03f), eye, dt);
            readout.SetAlpha(Mathf.Clamp01((prepClock - 0.25f) / 0.3f));
            chips.Begin();
            readout.Draw(chips);
            chips.End();
        }
    }

    void BeginRun(Vector3 eye)
    {
        restParent = holder.parent;
        restPosition = holder.localPosition;
        restRotation = holder.localRotation;
        restScale = holder.localScale;
        axisOrigin = CadLook.Flip(demo.axis?.origin, new Vector3(assembled.center.x, assembled.min.y, assembled.center.z));
        axisDir = CadLook.Flip(demo.axis?.dir, Vector3.up);
        if (axisDir.sqrMagnitude < 1e-8f) axisDir = Vector3.up;
        axisDir.Normalize();
        pivot = MarkerPosition("cg", assembled.center);
        tipAlong = 0f;
        for (int i = 0; i < 8; i++) tipAlong = Mathf.Max(tipAlong, Vector3.Dot(Corner(assembled, i) - axisOrigin, axisDir));
        clock = 0f;
        formatKey = null;
        formatPhase = null;
        HideLabels();
        RevealLift = RevealGlass = 0f;
        stage = Stage.Run;
        frame = new CadDemoFrame { kind = kind, phase = "", playback = 1f, readouts = kind == Launch ? null : demo.readouts };
        if (kind == Launch) BeginLaunch(eye);
        else if (kind == Hover) BeginHover();
        else if (kind == Walk) BeginWalk();
        else BeginSpin();
    }

    void BeginEnd()
    {
        endStart = clock;
        if (kind == Launch)
        {
            endFromPosition = holder.position;
            endFromRotation = holder.rotation;
        }
        else if (kind == Hover) endFromHeight = height;
        else if (kind == Walk)
        {
            endShare = walkShare;  // step back from where it is in the lap
            endLap = lapTime;
        }
        else
        {
            runEnd = Mathf.Min(runEnd, clock);  // stop now: slow down, then the lid comes back
            if (!stopping) BeginSpinDown(Stopping);
        }
        stage = Stage.End;
    }

    void Finish()
    {
        token++;
        Restore();
        stage = Stage.Idle;
        Changed?.Invoke(false);
    }

    void Restore()
    {
        if (holder != null && restParent != null && holder.parent == restParent)
        {
            holder.localPosition = restPosition;
            holder.localRotation = restRotation;
            holder.localScale = restScale;
        }
        restParent = null;
        for (int i = 0; i < spinners.Count; i++)
        {
            var s = spinners[i];
            if (s.transform == null) continue;
            s.transform.localPosition = s.restPosition;
            s.transform.localRotation = s.restRotation;
        }
        spinners.Clear();
        for (int i = 0; i < walkJoints.Count; i++)
        {
            var j = walkJoints[i];
            if (j.transform == null) continue;
            j.transform.localPosition = j.restPosition;
            j.transform.localRotation = j.restRotation;
        }
        walkJoints.Clear();
        stopping = false;
        RevealLift = RevealGlass = 0f;
        lines.Hide();
        marks.Hide();
        chips.Hide();
        HideLabels();
    }

    void HideLabels()
    {
        readout.Hide();
        tag1.Hide();
        tag2.Hide();
        tag3.Hide();
        chipIn.Hide();
        chipOut.Hide();
        slowTag.Hide();
    }

    void DrawChips()
    {
        readout.Draw(chips);
        tag1.Draw(chips);
        tag2.Draw(chips);
        tag3.Draw(chips);
        chipIn.Draw(chips);
        chipOut.Draw(chips);
        slowTag.Draw(chips);
    }

    Quaternion RestRotation => restParent != null ? restParent.rotation * restRotation : holder.rotation;

    // A holder-space point where it sits at rest (the turntable may have turned the rest pose since).
    Vector3 RestPoint(Vector3 local) => restParent != null
        ? restParent.TransformPoint(restPosition + restRotation * Vector3.Scale(restScale, local))
        : holder.TransformPoint(local);

    // Turn the holder to `rotation` and move it so its local point `pivotLocal` is at `world`.
    void Place(Vector3 world, Quaternion rotation, Vector3 pivotLocal)
    {
        holder.rotation = rotation;
        holder.position = world - holder.TransformVector(pivotLocal);
    }

    Vector3 MarkerPosition(string id, Vector3 fallback)
    {
        var markers = info?.analysis?.markerList;
        if (markers != null)
            foreach (var m in markers)
                if (m != null && m.id == id && m.pos != null) return CadLook.Flip(m.pos, fallback);
        return fallback;
    }

    float Value(string name, float fallback)
    {
        var values = demo?.values;
        return values != null && values.TryGetValue(name, out float v) ? v : fallback;
    }

    float HalfWidth() => Mathf.Max(assembled.extents.x, assembled.extents.z) * holder.lossyScale.x;

    static Vector3 Right(Vector3 eye, Vector3 at)
    {
        Vector3 r = Vector3.Cross(Vector3.up, at - eye);
        return r.sqrMagnitude > 1e-6f ? r.normalized : Vector3.right;
    }

    static Vector3 Corner(Bounds b, int i) => b.center + Vector3.Scale(b.extents,
        new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));

    // ---- Launch ----

    void BeginLaunch(Vector3 eye)
    {
        samples = flight.samples != null && flight.samples.Count >= 2 ? flight.samples : FromTrajectory(flight);
        if (samples != null) samples.RemoveAll(s => s == null);
        if (samples == null || samples.Count < 2)
        {
            Debug.LogWarning("[CadDemoPlayer] The flight has no samples or trajectory to fly.");
            Finish();
            return;
        }
        tLand = flight.landingTimeS > 0f ? flight.landingTimeS : samples[samples.Count - 1].t;
        tApogee = EventTime("apogee", flight.timeToApogeeS > 0f ? flight.timeToApogeeS : tLand * 0.12f);
        tBurnout = flight.burnoutTimeS > 0f ? flight.burnoutTimeS : EventTime("burnout", tApogee * 0.3f);
        tRail = flight.railExitTimeS > 0f ? flight.railExitTimeS : EventTime("rail_exit", 0.2f);
        deploys = flight.deployTimeS.HasValue && flight.deployTimeS.Value < tLand;
        tDeploy = deploys ? Mathf.Max(flight.deployTimeS.Value, tApogee) : tLand;
        tSwitch = deploys ? tDeploy : Mathf.Min(tLand, tApogee + 0.8f);  // real time until here, then ×8

        // Room scale: apogee launchHeight above the pad, at least 0.3 m under the ceiling; downrange on the same scale.
        float apogee = Mathf.Max(0.1f, flight.apogeeM), farthest = 0.01f;
        for (int i = 0; i < samples.Count; i++)
        {
            apogee = Mathf.Max(apogee, samples[i].altM);
            farthest = Mathf.Max(farthest, Mathf.Abs(samples[i].downrangeM));
        }
        float scale = holder.lossyScale.x;
        pad = RestPoint(axisOrigin);
        padPivot = RestPoint(pivot);
        float top = Mathf.Min(padPivot.y + launchHeight, CadLook.CeilingY() - 0.3f);
        vScale = Mathf.Max(0.4f, top - padPivot.y) / apogee;
        hScale = Mathf.Min(vScale, maxDrift / farthest);

        // Downrange runs away from you along the desk; to your right instead if that's a wall.
        Vector3 away = Vector3.ProjectOnPlane(padPivot - eye, Vector3.up);
        if (away.sqrMagnitude < 1e-4f) away = Vector3.forward;
        downDir = away.normalized;
        float drift = Mathf.Abs(samples[samples.Count - 1].downrangeM) * hScale;
        if (!CadLook.InRoom(padPivot + downDir * Mathf.Max(drift, 0.1f))) downDir = Right(eye, padPivot);
        float tilt = (90f - Value("inclination_deg", 85f)) * Mathf.Deg2Rad;
        railDir = (Vector3.up * Mathf.Cos(tilt) + downDir * Mathf.Sin(tilt)).normalized;

        // The whole flight, once: the predicted path and the trail are drawn from these points.
        pathCount = 0;
        for (float t = 0f; t < tApogee && pathCount < PathMax - 2; t += 0.06f) AddPath(t);
        AddPath(tApogee);
        apexIndex = pathCount - 1;
        float step = Mathf.Max(0.25f, (tLand - tApogee) / Mathf.Max(1, PathMax - pathCount - 2));
        for (float t = tApogee + step; t < tLand && pathCount < PathMax - 1; t += step) AddPath(t);
        AddPath(tLand);
        apex = path[apexIndex];
        burnoutPoint = PathAt(tBurnout);
        deployPoint = PathAt(tDeploy);
        landingPoint = PathAt(tLand);

        flightRotation = holder.rotation;
        travel = railDir;
        apexReached = false;
        landedAt = -1f;
        var inv = CultureInfo.InvariantCulture;
        float liftoff = flight.liftoffMassG > 0f ? flight.liftoffMassG : -1f;
        countdownSub = (string.IsNullOrEmpty(flight.motor) ? "Motor lit" : flight.motor) + (liftoff > 0f ? "  ·  " + liftoff.ToString("0", inv) + " g" : "");
        countdownFormat = "<b>T−{0:1} s</b>\n<size=75%><alpha=#A3>" + countdownSub;
        shownA = float.NaN;
        landedTitle = "Landed  " + tLand.ToString("0.0", inv) + " s";
        float driftM = flight.driftM > 0f ? flight.driftM : Mathf.Abs(samples[samples.Count - 1].downrangeM);
        landedSub = driftM.ToString("0", inv) + " m from the pad";
        tag1.SetStyle(CadLabel.Style.Event);
        tag1.SetText("Burnout  " + tBurnout.ToString("0.0", inv) + " s");
        tag2.SetStyle(CadLabel.Style.Event);
        tag2.SetText("Apogee  " + Mathf.Max(flight.apogeeM, apogee).ToString("0", inv) + " m", tApogee.ToString("0.0", inv) + " s  ·  predicted");
        tag3.SetStyle(CadLabel.Style.Event);
        tag3.SetText("Parachute  " + tDeploy.ToString("0.0", inv) + " s");
        frame.phase = "Countdown";
    }

    void AddPath(float t)
    {
        if (pathCount >= PathMax) return;
        pathT[pathCount] = t;
        path[pathCount] = PathAt(t);
        pathCount++;
    }

    float EventTime(string id, float fallback)
    {
        if (flight.events != null)
            foreach (var e in flight.events)
                if (e != null && e.id == id) return e.t;
        return fallback;
    }

    // Seconds since Run began to sim seconds: the countdown, real time to the parachute, then ×8.
    float FlightTime(float tau, out float playback)
    {
        playback = 1f;
        float u = tau - Countdown;
        if (u <= 0f) return 0f;
        if (u <= tSwitch) return u;
        playback = descentSpeedup;
        return Mathf.Min(tLand, tSwitch + (u - tSwitch) * descentSpeedup);
    }

    float LandingTau => Countdown + tSwitch + (tLand - tSwitch) / Mathf.Max(descentSpeedup, 1f);

    void Sample(float t, out float alt, out float down, out float spd)
    {
        int n = samples.Count;
        int lo = 0, hi = n - 1;
        if (t <= samples[0].t) hi = 0;
        else if (t >= samples[n - 1].t) lo = n - 1;
        else
            while (hi - lo > 1)
            {
                int mid = (lo + hi) >> 1;
                if (samples[mid].t <= t) lo = mid;
                else hi = mid;
            }
        var a = samples[Mathf.Min(lo, hi)];
        var b = samples[Mathf.Max(lo, hi)];
        float k = b.t > a.t ? Mathf.Clamp01((t - a.t) / (b.t - a.t)) : 0f;
        alt = Mathf.Max(0f, Mathf.Lerp(a.altM, b.altM, k));
        down = Mathf.Lerp(a.downrangeM, b.downrangeM, k);
        spd = Mathf.Lerp(a.speedMps, b.speedMps, k);
    }

    Vector3 PathAt(float t)
    {
        Sample(t, out float alt, out float down, out _);
        return padPivot + Vector3.up * (alt * vScale) + downDir * (down * hScale);
    }

    string PhaseAt(float t) =>
        t <= 0f ? "On the pad" : t < tBurnout ? "Boost" : t < tApogee - 0.35f ? "Coast" : t < tSwitch ? "Apogee" : t < tLand ? "Descent" : "Landed";

    void TickLaunch(float dt, Vector3 eye)
    {
        float tau = clock;
        float t = FlightTime(tau, out float playback);
        bool counting = stage == Stage.Run && tau < Countdown;
        bool flying = stage == Stage.Run && !counting && tau < LandingTau;
        float alt = 0f, spd = 0f;
        if (counting)
        {
            flightRotation = holder.rotation;  // on the pad, as the turntable left it
            frame.phase = "Countdown";
        }
        else if (flying)
        {
            Sample(t, out alt, out float down, out spd);
            Vector3 position = padPivot + Vector3.up * (alt * vScale) + downDir * (down * hScale);
            float h = Mathf.Max(flight.sampleDtS, 0.05f);
            Vector3 ahead = PathAt(t + h) - PathAt(Mathf.Max(0f, t - h));
            if (ahead.sqrMagnitude > 1e-10f) travel = ahead.normalized;
            bool chute = deploys && t >= tDeploy;
            Quaternion rest = RestRotation;
            // Locked to the rail until it leaves it; nose-up under the canopy; in between the nose follows the path.
            Vector3 want = t < tRail ? railDir : chute ? Vector3.up : travel;
            Quaternion target = Quaternion.FromToRotation(rest * axisDir, want) * rest;
            if (chute)
            {
                float swing = Mathf.Clamp01((tau - Countdown - tSwitch) / 0.6f);
                target = Quaternion.AngleAxis(5f * swing * Mathf.Sin((tau - Countdown - tSwitch) * 3.4f), Vector3.Cross(Vector3.up, downDir)) * target;
            }
            flightRotation = Quaternion.Slerp(flightRotation, target, 1f - Mathf.Exp(-dt * (chute ? 6f : 16f)));
            Place(position, flightRotation, pivot);
            if (t >= tApogee) apexReached = true;
            frame.phase = PhaseAt(t);
        }
        else if (stage == Stage.Run)
        {
            if (landedAt < 0f) landedAt = tau;
            frame.phase = "Landed";
            if (tau - landedAt >= LandedHold) BeginEnd();
        }
        if (stage == Stage.End)
        {
            float k = Mathf.Clamp01((clock - endStart) / HomeSeconds), e = CadLook.EaseInOut(k);
            Vector3 home = restParent.TransformPoint(restPosition);
            float hop = Mathf.Min(0.05f, Vector3.Distance(endFromPosition, home) * 0.3f);
            holder.SetPositionAndRotation(Vector3.Lerp(endFromPosition, home, e) + Vector3.up * (hop * Mathf.Sin(k * Mathf.PI)),
                Quaternion.Slerp(endFromRotation, RestRotation, e));
            if (k >= 1f)
            {
                Finish();
                return;
            }
        }
        frame.timeS = t;
        frame.altitudeM = alt;
        frame.speedMps = spd;
        frame.progress = Mathf.Clamp01(t / Mathf.Max(tLand, 1e-3f));
        frame.playback = playback;
        frame.countdownS = counting ? Countdown - tau : 0f;
        DrawLaunch(tau, t, counting, flying, playback, alt, spd, eye, dt);
    }

    void DrawLaunch(float tau, float t, bool counting, bool flying, float playback, float alt, float spd, Vector3 eye, float dt)
    {
        float fade = stage == Stage.End ? 1f - Mathf.Clamp01((clock - endStart) / HomeSeconds) : 1f;
        Color hueLine = Color.Lerp(Color.white, CadLook.Hue, 0.45f);

        // The pad: a ring of light that pulses three times in the countdown, its pool, and the rail.
        float pulse = counting ? 0.5f + 0.5f * Mathf.Cos(tau * 2f * Mathf.PI / 0.4f) : 0.4f;
        lines.Disc(pad, Vector3.up, 0.1f, CadLook.Alpha(CadLook.Hue, (0.25f + 0.35f * pulse) * fade));
        lines.Circle(pad, Vector3.up, 0.075f, CadLook.Line, CadLook.Alpha(hueLine, (0.6f + 0.4f * pulse) * fade), 48);
        float length = tipAlong * holder.lossyScale.x;
        lines.Segment(pad, pad + railDir * (length * 1.25f), CadLook.ThinLine, CadLook.Alpha(Color.white, (counting ? 0.8f : 0.35f) * fade));

        // The sim's predicted path, faint and dashed; the part flown so far burns bright (dashed on the way down).
        lines.DashStart(0.002f, 0.006f);
        for (int i = 1; i <= apexIndex; i++) lines.Dash(path[i - 1], path[i], CadLook.ThinLine, CadLook.Alpha(Color.white, 0.32f * fade));
        lines.DashStart(0.002f, 0.007f);
        lines.Dash(apex, deployPoint, CadLook.ThinLine, CadLook.Alpha(Color.white, 0.18f * fade));
        lines.Dash(deployPoint, landingPoint, CadLook.ThinLine, CadLook.Alpha(Color.white, 0.18f * fade));
        if (!counting)
        {
            Vector3 now = flying ? PathAt(t) : landingPoint;
            Color bright = CadLook.Alpha(hueLine, 0.95f * fade);
            int i = 1;
            for (; i <= apexIndex && pathT[i] <= t; i++) lines.Segment(path[i - 1], path[i], CadLook.Line, bright);
            if (t < tApogee && i <= apexIndex) lines.Segment(path[i - 1], now, CadLook.Line, bright);
            if (t > tApogee)
            {
                Color dashed = CadLook.Alpha(hueLine, 0.55f * fade);
                lines.DashStart(0.003f, 0.005f);
                int j = apexIndex + 1;
                for (; j < pathCount && pathT[j] <= t; j++) lines.Dash(path[j - 1], path[j], CadLook.ThinLine, dashed);
                if (j < pathCount) lines.Dash(path[j - 1], now, CadLook.ThinLine, dashed);
            }
        }

        // The motor's flame while it burns.
        if (flying && t < tBurnout)
        {
            Vector3 back = -holder.TransformDirection(axisDir);
            Vector3 nozzle = holder.TransformPoint(axisOrigin);
            float flicker = 0.75f + 0.5f * Mathf.PerlinNoise(Time.time * 23f, 0.37f);
            float burn = 1f - Mathf.Clamp01((t - (tBurnout - 0.25f)) / 0.25f);
            Color warm = Color.Lerp(CadLook.Hue, Color.white, 0.35f);
            lines.Dot(nozzle + back * 0.006f, 0.024f * flicker, CadLook.Alpha(warm, 0.9f * burn));
            lines.Segment(nozzle, nozzle + back * (0.05f * flicker), 0.009f, CadLook.Alpha(warm, 0.8f * burn), CadLook.Alpha(CadLook.Hue, 0f));
        }

        // The parachute: opens over 0.35 s at the deploy event, falls away on landing (or when stopped early).
        float deployTau = Countdown + tDeploy;
        if (deploys && !counting && tau >= deployTau)
        {
            float open = CadLook.Smooth((tau - deployTau) / ChuteOpen);
            float fallStart = landedAt >= 0f ? landedAt : stage == Stage.End ? endStart : -1f;
            float fall = fallStart >= 0f ? Mathf.Clamp01((clock - fallStart) / 0.45f) : 0f;
            if (fall < 1f) DrawChute(open * (1f - 0.5f * fall), (1f - fall) * fade, hueLine);
        }

        // Events: a bead and a tag at burnout, the apogee ring with its tag (predicted until reached), the parachute.
        Vector3 right = Right(eye, apex);
        if (!counting && t >= tBurnout)
        {
            marks.Dot(burnoutPoint, 0.0065f, CadLook.Alpha(Color.white, 0.9f * fade));
            tag1.SetAnchor(1);
            tag1.MoveTo(burnoutPoint + right * 0.01f, eye, dt, snap: true);
            tag1.SetAlpha(fade);
        }
        else tag1.Hide();
        if (apexReached && tag2.CurrentStyle != CadLabel.Style.EventOn)
        {
            tag2.SetStyle(CadLabel.Style.EventOn);
            var inv = CultureInfo.InvariantCulture;
            tag2.SetText("Apogee  " + flight.apogeeM.ToString("0", inv) + " m", tApogee.ToString("0.0", inv) + " s");
        }
        marks.Ring(apex, 0.0065f, CadLook.ThinLine, CadLook.Alpha(hueLine, (apexReached ? 1f : 0.55f) * fade));
        tag2.SetAnchor(1);
        tag2.MoveTo(apex + right * 0.014f, eye, dt, snap: true);
        tag2.SetAlpha(fade);
        if (deploys && !counting && t >= tDeploy)
        {
            marks.Dot(deployPoint, 0.0055f, CadLook.Alpha(Color.white, 0.8f * fade));
            tag3.SetAnchor(-1);
            tag3.MoveTo(deployPoint - right * 0.012f, eye, dt, snap: true);
            tag3.SetAlpha(fade);
        }
        else tag3.Hide();

        // The readout chip rides beside the rocket: left of it in flight, right of it on the pad and landed.
        Vector3 at = holder.TransformPoint(pivot);
        Vector3 side = Right(eye, at);
        float half = HalfWidth() + 0.026f;
        if (counting)
        {
            ShowNumbers(countdownFormat, Mathf.Round((Countdown - tau) * 10f) / 10f, 0f, 0f, false);
            readout.SetAnchor(1);
            readout.MoveTo(at + side * half, eye, dt, snap: true);
        }
        else if (flying)
        {
            // Keyed on the phase and whether the descent is sped up: no string built per frame.
            bool fast = playback > 1f;
            bool changed = frame.phase != formatPhase || fast != formatFast;
            if (changed)
            {
                formatPhase = frame.phase;
                formatFast = fast;
                formatKey = null;
                readoutFormat = "<b>{0:0} m  ·  {1:0} m/s</b>\n<size=75%><alpha=#A3>" + frame.phase +
                                (playback > 1f ? "  ·  descent ×" + Mathf.RoundToInt(playback) : "") + "  ·  T+{2:1} s";
            }
            ShowNumbers(readoutFormat, Mathf.Round(alt), Mathf.Round(spd), Mathf.Round(t * 10f) / 10f, changed);
            readout.SetAnchor(-1);
            readout.MoveTo(at - side * half, eye, dt, snap: true);
        }
        else
        {
            if (formatKey != "landed")
            {
                formatKey = "landed";
                readout.SetText(landedTitle, landedSub);
            }
            readout.SetAnchor(1);
            readout.MoveTo(at + side * half, eye, dt, snap: true);
        }
        readout.SetAlpha(Mathf.Clamp01(clock / 0.25f) * fade);
    }

    string readoutFormat, countdownFormat;
    float shownA = float.NaN, shownB, shownC;

    // Live numbers on the readout, rebuilt only when what it shows changes (a few times a second), without allocating.
    void ShowNumbers(string format, float a, float b, float c, bool force)
    {
        if (!force && a == shownA && b == shownB && c == shownC) return;
        shownA = a;
        shownB = b;
        shownC = c;
        readout.SetFormat(format, a, b, c);
    }

    // A canopy ring with six arcs over its dome and shroud lines down to the nose (CAD UX spec 4.1), 85 mm.
    void DrawChute(float open, float alpha, Color hueLine)
    {
        if (alpha <= 0.003f || open <= 0.01f) return;
        Vector3 up = holder.TransformDirection(axisDir);
        Vector3 nose = holder.TransformPoint(axisOrigin + axisDir * tipAlong);
        Vector3 u = Vector3.ProjectOnPlane(Vector3.forward, up);
        if (u.sqrMagnitude < 1e-6f) u = Vector3.ProjectOnPlane(Vector3.right, up);
        u.Normalize();
        Vector3 w = Vector3.Cross(up, u);
        Vector3 top = nose + up * (0.13f * open);
        Vector3 rimCentre = top - up * (0.028f * open);
        Vector3 dome = top + up * (0.03f * open);
        float r = 0.085f * open;
        Color rim = CadLook.Alpha(hueLine, 0.95f * alpha), arc = CadLook.Alpha(hueLine, 0.8f * alpha), cord = CadLook.Alpha(Color.white, 0.5f * alpha);
        lines.Circle(rimCentre, up, r, CadLook.Line, rim, 24);
        for (int i = 0; i < 6; i++)
        {
            float a = i * Mathf.PI / 3f;
            Vector3 p = rimCentre + (u * Mathf.Cos(a) + w * Mathf.Sin(a)) * r;
            Vector3 mid = Vector3.Lerp(p, dome, 0.5f) + up * (0.012f * open);
            lines.Segment(p, mid, CadLook.ThinLine, arc);
            lines.Segment(mid, dome, CadLook.ThinLine, arc);
            lines.Segment(p, nose, CadLook.ThinLine * 0.8f, cord);
        }
    }

    // Flights stored before round 4 have only the climb (trajectory_m): make samples of it, and a straight way down.
    static List<CadFlightSample> FromTrajectory(CadFlight f)
    {
        var points = f.trajectory;
        if (points == null || points.Count < 2) return null;
        float tApogee = f.timeToApogeeS > 0f ? f.timeToApogeeS : 6f;
        var list = new List<CadFlightSample>(points.Count + 32);
        for (int i = 0; i < points.Count; i++)
        {
            var p = points[i];
            if (p == null || p.Length < 3) continue;
            list.Add(new CadFlightSample
            {
                t = tApogee * i / (points.Count - 1),
                altM = p[1],
                downrangeM = Mathf.Sqrt(p[0] * p[0] + p[2] * p[2]),
            });
        }
        if (list.Count < 2) return null;
        for (int i = 1; i < list.Count; i++)
        {
            var a = list[i - 1];
            var b = list[i];
            float dt = Mathf.Max(b.t - a.t, 1e-3f);
            b.vzMps = (b.altM - a.altM) / dt;
            b.speedMps = new Vector2(b.downrangeM - a.downrangeM, b.altM - a.altM).magnitude / dt;
        }
        var top = list[list.Count - 1];
        const float rate = 5f;  // under a parachute
        float fall = Mathf.Max(top.altM, 1f) / rate;
        for (int k = 1; k <= 30; k++)
        {
            float s = k / 30f;
            list.Add(new CadFlightSample { t = tApogee + fall * s, altM = top.altM * (1f - s), downrangeM = top.downrangeM, vzMps = -rate, speedMps = rate });
        }
        if (f.landingTimeS <= 0f)
        {
            f.landingTimeS = tApogee + fall;
            if (!f.deployTimeS.HasValue) f.deployTimeS = tApogee;
        }
        return list;
    }

    // ---- Hover ----

    void BeginHover()
    {
        if (demo.axis?.origin != null) pivot = axisOrigin;  // the thrust centre
        BuildSpinners(propTurnsPerSecond * 60f);
        height = 0f;
        float max = Value("max_thrust_g", 0f), need = Value("hover_thrust_g", 0f);
        thrustShare = max > 0f && need > 0f ? Mathf.Clamp01(need / max) : Mathf.Clamp01(Mathf.Pow(Value("hover_throttle_pct", 50f) / 100f, 2f));
        var inv = CultureInfo.InvariantCulture;
        float spare = Value("thrust_margin_pct", -1f), rpm = Value("hover_rpm", -1f);
        hoverSub = spare >= 0f && rpm >= 0f
            ? spare.ToString("0", inv) + "% thrust to spare  ·  " + CadLook.Thousands(rpm) + " rpm"
            : demo.readouts != null && demo.readouts.Count > 0 && demo.readouts[0] != null ? demo.readouts[0].label + "  " + demo.readouts[0].display : "";
        tag1.SetStyle(CadLabel.Style.Event);
        tag1.SetText(max > 0f ? "max " + (max >= 1000f ? (max / 1000f).ToString("0.00", inv) + " kg" : max.ToString("0", inv) + " g") : "max thrust");
        tag2.SetStyle(CadLabel.Style.Event);
        tag2.SetText(need > 0f ? "hover " + need.ToString("0", inv) + " g" : "hover");
        tag3.SetStyle(CadLabel.Style.Event);
        float throttle = Value("hover_throttle_pct", -1f);
        hoverTitle = throttle >= 0f ? "Hovering  ·  " + throttle.ToString("0", inv) + "% throttle" : "Hovering";
        shownA = float.NaN;
        frame.phase = "Spin-up";
    }

    void TickHover(float dt, Vector3 eye)
    {
        float tau = clock, hold = TakeOff + SpinUp;
        float landAt = hold + Mathf.Max(1f, hoverSeconds);
        float lift = 0f;
        if (stage == Stage.Run)
        {
            if (tau < SpinUp)
            {
                speed = CadLook.EaseInOut(tau / SpinUp);
                frame.phase = "Spin-up";
            }
            else
            {
                speed = 1f;
                lift = CadLook.EaseOut((tau - SpinUp) / TakeOff);
                frame.phase = tau < hold ? "Take-off" : "Hover";
            }
            float air = Mathf.Max(0f, tau - hold);
            height = hoverHeight * lift + lift * 0.008f * Mathf.Sin(2f * Mathf.PI * air / 2.4f);
            if (tau >= landAt) BeginEnd();
        }
        if (stage == Stage.End)
        {
            float k = clock - endStart;
            height = Mathf.Lerp(endFromHeight, 0f, CadLook.EaseInOut(k / LandSeconds));
            lift = Mathf.Clamp01(height / Mathf.Max(hoverHeight, 1e-3f));
            if (k < LandSeconds) frame.phase = "Landing";
            else
            {
                if (!stopping) BeginSpinDown(SpinDown);
                frame.phase = "Spin-down";
            }
        }
        // Level as it rises (the stage tips models back to show their top), with a gentle yaw sway in the air.
        Quaternion rest = RestRotation;
        Quaternion level = Quaternion.FromToRotation(rest * axisDir, Vector3.up) * rest;
        float up = Mathf.Clamp01(height / Mathf.Max(hoverHeight * 0.5f, 1e-3f));
        float yaw = 4f * lift * Mathf.Sin(2f * Mathf.PI * Mathf.Max(0f, tau - SpinUp - TakeOff) / 5f);
        Quaternion rotation = Quaternion.AngleAxis(yaw, Vector3.up) * Quaternion.Slerp(rest, level, up);
        Vector3 restPivot = RestPoint(pivot);
        Place(restPivot + Vector3.up * height, rotation, pivot);
        if (Turn(dt) && stage == Stage.End)
        {
            Finish();
            return;
        }
        DrawHover(tau, restPivot, eye, dt);
        frame.timeS = tau;
        frame.altitudeM = height;
        frame.speedMps = 0f;
        frame.progress = speed;
    }

    void DrawHover(float tau, Vector3 restPivot, Vector3 eye, float dt)
    {
        float scale = holder.lossyScale.x, k = speed;
        float fade = stage == Stage.End ? 1f - Mathf.Clamp01((clock - endStart - LandSeconds) / 0.6f) : Mathf.Clamp01(tau / 0.3f);
        Vector3 right = Right(eye, holder.position);
        Color hueLine = Color.Lerp(Color.white, CadLook.Hue, 0.4f);
        int tagged = spinners.Count > 1 ? 1 : 0;
        for (int i = 0; i < spinners.Count; i++)
        {
            var s = spinners[i];
            Vector3 c = holder.TransformPoint(s.originHolder), axis = holder.TransformDirection(s.axisHolder);
            if (s.radius > 0f && k > 0.02f)
            {
                // Props blur into glowing discs, warm at the tips.
                lines.Disc(c, axis, s.radius * scale, CadLook.Alpha(Color.white, 0.09f * k));
                lines.Circle(c, axis, s.radius * scale, CadLook.ThinLine, CadLook.Alpha(Warm, 0.55f * k), 36);
            }
            // Thrust to scale: dashed to the motor's max, bright to what hovering needs.
            Vector3 b0 = c + Vector3.up * (0.008f * scale), b1 = b0 + Vector3.up * (0.15f * scale), bn = b0 + Vector3.up * (0.15f * scale * thrustShare);
            lines.Dashed(b0, b1, CadLook.ThinLine, CadLook.Alpha(Color.white, 0.42f * k), 0.003f, 0.005f);
            lines.Segment(b1 - right * 0.005f, b1 + right * 0.005f, CadLook.ThinLine, CadLook.Alpha(Color.white, 0.6f * k));
            lines.Segment(b0, bn, 0.006f, CadLook.Alpha(hueLine, 0.95f * k));
            if (i == tagged)
            {
                tag1.SetAnchor(1);
                tag1.MoveTo(b1 + right * 0.008f, eye, dt, snap: true);
                tag1.SetAlpha(k > 0.5f ? k * fade : 0f);
                tag2.SetAnchor(1);
                tag2.MoveTo(bn + right * 0.009f, eye, dt, snap: true);
                tag2.SetAlpha(k > 0.5f ? k * fade : 0f);
            }
        }
        // How high: a dashed line down to the desk under it.
        float above = Vector3.Dot(pivot - new Vector3(pivot.x, assembled.min.y, pivot.z), Vector3.up) * scale;
        Vector3 body = holder.TransformPoint(pivot), ground = restPivot - Vector3.up * above;
        if (height > 0.01f)
        {
            lines.Dashed(body, ground, CadLook.ThinLine, CadLook.Alpha(Color.white, 0.45f * fade), 0.002f, 0.004f);
            float cm = Mathf.Round(height * 100f);
            if (cm != shownA)
            {
                shownA = cm;
                tag3.SetFormat("<b>{0:0} cm</b>", cm, 0f, 0f);
            }
            tag3.SetAnchor(1);
            tag3.MoveTo((body + ground) * 0.5f + right * 0.01f, eye, dt, snap: true);
            tag3.SetAlpha(fade);
        }
        else tag3.Hide();
        // The readout, up and to the right of it.
        string title = frame.phase == "Hover" ? hoverTitle : frame.phase;
        if (formatKey != title)
        {
            formatKey = title;
            readout.SetText(title, hoverSub);
        }
        Vector3 topPoint = holder.TransformPoint(new Vector3(assembled.center.x, assembled.max.y, assembled.center.z));
        readout.SetAnchor(1);
        readout.MoveTo(topPoint + right * (HalfWidth() + 0.03f) + Vector3.up * 0.03f, eye, dt);
        readout.SetAlpha(fade);
    }

    // ---- Spin ----

    void BeginSpin()
    {
        BuildSpinners(-1f);
        lid = 0f;
        runEnd = LidUp + Ramp + Mathf.Max(1f, spinSeconds);
        var inv = CultureInfo.InvariantCulture;
        float inRpm = Value("input_rpm", -1f), outRpm = Value("output_rpm", -1f);
        float inNm = Value("input_torque_nm", -1f), outNm = Value("output_torque_nm", -1f), ratio = Value("ratio", -1f);
        chipIn.SetStyle(CadLabel.Style.Read);
        chipIn.SetText(inRpm >= 0f ? "In  " + CadLook.Thousands(inRpm) + " rpm" : "Input",
            inNm >= 0f ? inNm.ToString("0.00", inv) + " N·m" : null);
        chipOut.SetStyle(CadLabel.Style.Read);
        chipOut.SetText(outRpm >= 0f ? "Out  " + CadLook.Thousands(outRpm) + " rpm" : "Output",
            outNm >= 0f ? outNm.ToString("0.00", inv) + " N·m" + (ratio > 0f ? "  ·  " + ratio.ToString("0.00", inv) + " : 1" : "") : null);
        slowTag.SetStyle(CadLabel.Style.Tag);
        slowTag.SetText("Slow motion  ·  " + Mathf.RoundToInt(slowdown) + "× slower");
        frame.phase = "Opening";
    }

    float slowdown = 1f;

    void TickSpin(float dt, Vector3 eye)
    {
        float tau = clock;
        float stopEnd = runEnd + Stopping;
        // The lid: lifts on a spring (omega 9, zeta 0.85), back once the train has stopped.
        lid = tau < stopEnd ? CadLook.SpringAt(tau, 9f, 0.85f) : 1f - CadLook.SpringAt(tau - stopEnd, 9f, 1f);
        if (tau >= stopEnd + LidDown) lid = 0f;
        RevealLift = 0.55f * Mathf.Clamp01(lid);
        RevealGlass = 0.45f * Mathf.Clamp01(lid);
        if (stage == Stage.Run)
        {
            if (tau < LidUp)
            {
                speed = 0f;
                frame.phase = "Opening";
            }
            else if (tau < LidUp + Ramp)
            {
                speed = (tau - LidUp) / Ramp;
                frame.phase = "Spin-up";
            }
            else
            {
                speed = 1f;
                frame.phase = "Running";
            }
            if (tau >= runEnd) BeginEnd();
        }
        if (stage == Stage.End)
        {
            frame.phase = tau < stopEnd ? "Stopping" : "Closing";
            if (tau >= stopEnd + LidDown)
            {
                Finish();
                return;
            }
        }
        Turn(dt);
        DrawSpin(tau, eye, dt);
        frame.timeS = tau;
        frame.altitudeM = 0f;
        frame.speedMps = 0f;
        frame.progress = speed;
    }

    void DrawSpin(float tau, Vector3 eye, float dt)
    {
        float scale = holder.lossyScale.x;
        Color hueLine = Color.Lerp(Color.white, CadLook.Hue, 0.4f);
        // The mesh: dashed pitch circles while the lid is up.
        if (lid > 0.5f)
        {
            Color dash = CadLook.Alpha(Color.white, 0.5f * lid);
            for (int i = 0; i < spinners.Count; i++)
            {
                var s = spinners[i];
                if (s.pitch <= 0f) continue;
                Vector3 c = holder.TransformPoint(s.centreHolder), axis = holder.TransformDirection(s.axisHolder);
                Vector3 u = Vector3.ProjectOnPlane(Mathf.Abs(axis.y) < 0.9f ? Vector3.up : Vector3.right, axis).normalized;
                Vector3 w = Vector3.Cross(axis, u);
                float r = s.pitch * scale;
                lines.DashStart(0.004f, 0.004f);
                Vector3 previous = c + u * r;
                for (int k = 1; k <= 48; k++)
                {
                    float a = k * 2f * Mathf.PI / 48f;
                    Vector3 next = c + (u * Mathf.Cos(a) + w * Mathf.Sin(a)) * r;
                    lines.Dash(previous, next, CadLook.ThinLine, dash);
                    previous = next;
                }
            }
        }
        // Rotation arrows round the shaft ends marked input / output, and their chips.
        var markers = info?.analysis?.markerList;
        Vector3 centre = holder.TransformPoint(assembled.center);
        float k2 = Mathf.Max(0.25f, speed);
        bool inShown = false, outShown = false;
        if (markers != null)
        {
            foreach (var m in markers)
            {
                if (m == null || m.pos == null || (m.kind != "input" && m.kind != "output")) continue;
                Vector3 p = CadLook.Flip(m.pos, Vector3.zero);
                int best = -1;
                float thinnest = float.MaxValue;
                for (int i = 0; i < spinners.Count; i++)
                {
                    var s = spinners[i];
                    if (Vector3.ProjectOnPlane(p - s.originHolder, s.axisHolder).magnitude > 0.006f) continue;
                    if (s.radius < thinnest)
                    {
                        thinnest = s.radius;
                        best = i;
                    }
                }
                Vector3 end = holder.TransformPoint(p);
                if (best >= 0)
                {
                    var sp = spinners[best];
                    Vector3 axis = holder.TransformDirection(sp.axisHolder);
                    float radius = (sp.radius + 0.009f) * scale;
                    float sense = sp.ratio >= 0f ? 1f : -1f;
                    Vector3 reference = holder.TransformDirection(Vector3.ProjectOnPlane(Mathf.Abs(sp.axisHolder.y) < 0.9f ? Vector3.up : Vector3.forward, sp.axisHolder));
                    float a0 = 17f, a1 = a0 + sense * 264f;
                    lines.Arc(end, axis, reference, radius, a0, a1, CadLook.Line, CadLook.Alpha(hueLine, 0.35f * k2), CadLook.Alpha(hueLine, k2), 28);
                    Arrowhead(end, axis, reference, radius, a1, sense, CadLook.Alpha(Color.white, k2));
                }
                // Its chip, out from the model with a leader back to the shaft end.
                Vector3 outward = Vector3.ProjectOnPlane(end - centre, Vector3.up);
                if (outward.sqrMagnitude < 1e-6f) outward = Right(eye, centre);
                outward.Normalize();
                Vector3 right = Right(eye, centre);
                int side = Vector3.Dot(outward, right) >= 0f ? 1 : -1;
                bool input = m.kind == "input";
                if (input ? inShown : outShown) continue;  // one chip each
                if (input) inShown = true;
                else outShown = true;
                var chip = input ? chipIn : chipOut;
                Vector3 at = end + outward * 0.05f + Vector3.up * 0.035f;
                chip.SetAnchor(side);
                chip.MoveTo(at, eye, dt);
                chip.SetAlpha(Mathf.Clamp01(lid * 1.5f));
                marks.Segment(end, chip.Edge(-side), CadLook.ThinLine, CadLook.Alpha(Color.white, 0.6f * lid));
            }
        }
        if (!inShown) chipIn.Hide();
        if (!outShown) chipOut.Hide();
        // "Slow motion" over the top.
        Vector3 topPoint = holder.TransformPoint(new Vector3(assembled.center.x, assembled.max.y, assembled.center.z));
        slowTag.SetAnchor(0);
        slowTag.MoveTo(topPoint + Vector3.up * 0.06f, eye, dt);
        slowTag.SetAlpha(lid);
    }

    // An open arrowhead at angle `a` on the arc, pointing the way it turns.
    void Arrowhead(Vector3 centre, Vector3 axis, Vector3 reference, float radius, float a, float sense, Color color)
    {
        axis.Normalize();
        Vector3 u = Vector3.ProjectOnPlane(reference, axis).normalized, w = Vector3.Cross(axis, u);
        float r = a * Mathf.Deg2Rad;
        Vector3 radial = u * Mathf.Cos(r) + w * Mathf.Sin(r);
        Vector3 tangent = (-u * Mathf.Sin(r) + w * Mathf.Cos(r)) * sense;
        Vector3 tip = centre + radial * radius;
        float size = Mathf.Max(0.005f, radius * 0.35f);
        lines.Segment(tip, tip - tangent * size + radial * (size * 0.6f), CadLook.ThinLine, color);
        lines.Segment(tip, tip - tangent * size - radial * (size * 0.6f), CadLook.ThinLine, color);
    }

    // ---- Walk ----

    // The motion in holder space: each joint's pivot and axis in its parent's space at rest (so a turned hip carries the
    // knee's pivot with it), and the model's path, mirrored like every model-frame point (glTFast flips X: a turn by a
    // about axis d in glTF is a turn by -a about the flipped d here, the quaternion (x, -y, -z, w)).
    void BeginWalk()
    {
        walkJoints.Clear();
        var m = motion;
        foreach (var j in m.joints)
        {
            if (j == null || j.anglesDeg == null || string.IsNullOrEmpty(j.id) || !model.objects.TryGetValue(j.id, out var go) || go == null) continue;
            var t = go.transform;
            var parent = t.parent;
            if (parent == null) continue;
            Vector3 o = CadLook.Flip(j.origin, Vector3.zero), d = CadLook.Flip(j.dir, Vector3.zero);
            if (d.sqrMagnitude < 1e-8f) continue;
            walkJoints.Add(new WalkJoint
            {
                transform = t,
                restPosition = t.localPosition,
                restRotation = t.localRotation,
                origin = parent.InverseTransformPoint(holder.TransformPoint(o)),
                axis = parent.InverseTransformVector(holder.TransformVector(d.normalized)).normalized,
                angles = j.anglesDeg,
            });
        }
        walkCount = m.samples > 0 ? m.samples : walkJoints.Count > 0 ? walkJoints[0].angles.Length : 0;
        walkRate = m.rateHz > 0f ? m.rateHz : 50f;
        lapSeconds = walkCount / walkRate;
        Vector3 shift = Vector3.zero;
        if (walkAroundBase && m.centre != null)
        {
            Vector3 c = CadLook.Flip(m.centre, Vector3.zero);
            shift = new Vector3(-c.x, 0f, -c.z);
        }
        walkPos = new Vector3[walkCount];
        walkRot = new Quaternion[walkCount];
        var root = m.root;
        for (int i = 0; i < walkCount; i++)
        {
            float[] p = root?.pos != null && i < root.pos.Count ? root.pos[i] : null;
            float[] q = root?.rot != null && i < root.rot.Count ? root.rot[i] : null;
            walkPos[i] = (p != null ? CadLook.Flip(p, Vector3.zero) : Vector3.zero) + shift;
            walkRot[i] = q != null && q.Length >= 4 ? new Quaternion(q[0], -q[1], -q[2], q[3]).normalized : Quaternion.identity;
        }
        walkLoad = m.tracks != null && m.tracks.TryGetValue("load_pct", out var load) && load != null && load.Length == walkCount ? load : null;
        float radiusMm = m.values != null && m.values.TryGetValue("radius_mm", out float r) ? r : 0f;
        lapRadius = radiusMm / 1000f;
        walkSpeed = Value("speed_m_s", m.values != null && m.values.TryGetValue("speed_m_s", out float v) ? v : 0f);
        walkShare = lapTime = 0f;
        var inv = CultureInfo.InvariantCulture;
        walkTitle = walkSpeed > 0f ? "Walking  ·  " + walkSpeed.ToString("0.00", inv) + " m/s" : "Walking";
        float steps = Value("step_rate_hz", 0f);
        walkSub = steps > 0f ? (steps * 60f).ToString("0", inv) + " steps a minute  ·  Unitree's trained gait" : "Unitree's trained gait";
        tag1.SetStyle(CadLabel.Style.Event);
        shownA = float.NaN;
        frame.phase = "Stepping out";
        if (walkCount < 2 || walkJoints.Count == 0)
        {
            Debug.LogWarning("[CadDemoPlayer] The walk has no joints or samples to play.");
            BeginEnd();
        }
    }

    void TickWalk(float dt, Vector3 eye)
    {
        float tau = clock;
        if (stage == Stage.Run)
        {
            if (tau < StepOut)
            {
                walkShare = CadLook.EaseInOut(tau / StepOut);
                lapTime = 0f;
                frame.phase = "Stepping out";
            }
            else
            {
                walkShare = 1f;
                lapTime = tau - StepOut;
                frame.phase = "Walking";
                if (lapTime >= lapSeconds)
                {
                    lapTime = 0f;  // the lap closes where it began
                    BeginEnd();
                }
            }
        }
        if (stage == Stage.End)
        {
            float k = (clock - endStart) / StepBack;
            if (k >= 1f)
            {
                Finish();
                return;
            }
            walkShare = endShare * (1f - CadLook.EaseInOut(k));
            lapTime = endLap;
            frame.phase = "Stepping back";
        }
        PoseWalk(lapTime, walkShare, out float load);
        DrawWalk(eye, dt, load);
        frame.timeS = lapTime;
        frame.altitudeM = 0f;
        frame.speedMps = walkShare > 0.99f ? walkSpeed : 0f;
        frame.progress = lapSeconds > 0f ? Mathf.Clamp01(lapTime / lapSeconds) : 0f;
        frame.load = load;
    }

    // The lap at time t, blended in by `share` (0: the version's pose where it stands; 1: walking): the joints turned
    // about their pivots, the holder moved along the path. While it blends it lifts a little, as Fly glides home.
    void PoseWalk(float t, float share, out float load)
    {
        float f = t * walkRate;
        int i0 = Mathf.FloorToInt(f);
        float k = f - i0;
        i0 = ((i0 % walkCount) + walkCount) % walkCount;
        int i1 = (i0 + 1) % walkCount;
        for (int n = 0; n < walkJoints.Count; n++)
        {
            var j = walkJoints[n];
            if (j.transform == null) continue;
            var a = j.angles;
            float angle = i1 < a.Length ? Mathf.Lerp(a[i0], a[i1], k) * share : 0f;
            Quaternion q = Quaternion.AngleAxis(-angle, j.axis);
            j.transform.localPosition = j.origin + q * (j.restPosition - j.origin);
            j.transform.localRotation = q * j.restRotation;
        }
        Quaternion r0 = walkRot[i0], r1 = walkRot[i1];
        if (Quaternion.Dot(r0, r1) < 0f) r1 = new Quaternion(-r1.x, -r1.y, -r1.z, -r1.w);
        Quaternion rot = Quaternion.Slerp(Quaternion.identity, Quaternion.Slerp(r0, r1, k), share);
        Vector3 pos = Vector3.Lerp(walkPos[i0], walkPos[i1], k) * share;
        float hop = share > 0.001f && share < 0.999f ? Mathf.Sin(share * Mathf.PI) * Mathf.Min(0.03f, walkPos[0].magnitude * 0.3f) : 0f;
        holder.rotation = RestRotation * rot;
        holder.position = RestPoint(pos) + (restParent != null ? restParent.TransformDirection(Vector3.up) : Vector3.up) * (hop * holder.lossyScale.x);
        load = walkLoad != null ? Mathf.Lerp(walkLoad[i0], walkLoad[i1], k) : Value("peak_load_pct", 0f);
    }

    void DrawWalk(Vector3 eye, float dt, float load)
    {
        float scale = holder.lossyScale.x;
        float fade = Mathf.Clamp01(walkShare * 1.4f);
        // The lap: a dashed circle on the base round its centre.
        if (lapRadius > 0f && walkAroundBase)
        {
            Vector3 up = RestRotation * Vector3.up, centre = RestPoint(Vector3.zero) + up * 0.003f;  // just off the base
            Vector3 u = Vector3.ProjectOnPlane(Mathf.Abs(up.y) < 0.9f ? Vector3.up : Vector3.forward, up).normalized, w = Vector3.Cross(up, u);
            float rr = lapRadius * scale;
            Color dash = CadLook.Alpha(Color.Lerp(Color.white, CadLook.Hue, 0.55f), 0.8f * fade);
            lines.DashStart(0.007f, 0.006f);
            Vector3 previous = centre + u * rr;
            for (int n = 1; n <= 64; n++)
            {
                float a = n * 2f * Mathf.PI / 64f;
                Vector3 next = centre + (u * Mathf.Cos(a) + w * Mathf.Sin(a)) * rr;
                lines.Dash(previous, next, CadLook.Line, dash);
                previous = next;
            }
        }
        // The readout over its head (it walks round the base, so to one side it would cross the panels), and the
        // joint load under it (numbers only when they change: no allocations).
        string title = frame.phase == "Walking" ? walkTitle : frame.phase;
        if (formatKey != title)
        {
            formatKey = title;
            readout.SetText(title, walkSub);
        }
        Vector3 top = holder.TransformPoint(new Vector3(assembled.center.x, assembled.max.y, assembled.center.z));
        float far = Vector3.Distance(eye, top);  // chips keep their angular size, so the gaps grow with distance
        readout.SetAnchor(0);
        readout.MoveTo(top + Vector3.up * (0.012f + 0.075f * far), eye, dt);
        readout.SetAlpha(Mathf.Max(0.35f, fade));
        float shown = Mathf.Round(load);
        if (shown != shownA)
        {
            shownA = shown;
            tag1.SetFormat(load > 100f ? "Joint load  <b><color=#FF6B5A>{0:0}%</color></b>" : load > 60f ? "Joint load  <b><color=#FFB547>{0:0}%</color></b>" : "Joint load  <b>{0:0}%</b>", shown, 0f, 0f);
        }
        tag1.SetAnchor(0);
        tag1.MoveTo(top + Vector3.up * (0.008f + 0.028f * far), eye, dt);
        tag1.SetAlpha(walkShare > 0.5f ? fade : 0f);
    }

    // ---- Spinners (hover and spin) ----

    // Parts turn about their axes at their real ratios. Hover: the fastest at `fastestOnScreen` rpm (or its real speed
    // if slower). Spin (negative): everything at rpm ÷ spinSlowdown.
    void BuildSpinners(float fastestOnScreen)
    {
        spinners.Clear();
        phi = speed = 0f;
        stopping = false;
        symmetry = 0;
        referenceSpeed = 0f;
        slowdown = 1f;
        var list = demo.spinParts;
        if (list == null) return;
        float fastest = 0f;
        foreach (var s in list)
            if (s != null) fastest = Mathf.Max(fastest, Mathf.Abs(s.rpm));
        if (fastest < 1e-3f) return;
        float scale = fastestOnScreen > 0f ? Mathf.Min(1f, fastestOnScreen / fastest) : 1f / Mathf.Max(1f, spinSlowdown);
        slowdown = 1f / scale;
        referenceSpeed = fastest * scale * 6f;
        foreach (var s in list)
        {
            if (s == null || string.IsNullOrEmpty(s.id) || !model.objects.TryGetValue(s.id, out var go) || go == null) continue;
            var t = go.transform;
            var parent = t.parent;
            if (parent == null) continue;
            Vector3 o = CadLook.Flip(s.origin, Vector3.zero), d = CadLook.Flip(s.dir, Vector3.zero);
            if (d.sqrMagnitude < 1e-8f) continue;
            d.Normalize();
            int index = exploder != null ? exploder.IndexOf(s.id) : -1;
            Vector3 centre = index >= 0 ? holder.InverseTransformPoint(t.TransformPoint(exploder[index].centre)) : o;
            spinners.Add(new Spinner
            {
                id = s.id,
                transform = t,
                restPosition = t.localPosition,
                restRotation = t.localRotation,
                origin = parent.InverseTransformPoint(holder.TransformPoint(o)),
                axis = parent.InverseTransformVector(holder.TransformVector(d)).normalized,
                originHolder = o,
                axisHolder = d,
                centreHolder = o + d * Vector3.Dot(centre - o, d),
                // + rpm is counter-clockwise seen from the tip of dir; Unity's AngleAxis turns clockwise for +.
                ratio = -s.rpm / fastest,
                radius = s.radiusMm.HasValue && s.radiusMm.Value > 0f ? s.radiusMm.Value / 1000f : CadLook.PartRadius(t, holder, o, d),
                pitch = s.radiusMm.HasValue && s.radiusMm.Value > 0f ? s.radiusMm.Value / 1000f : 0f,
            });
        }
        // Gears: a spinner that reaches well past the thinnest one on its axis (its shaft). Without the hub's
        // radius_mm, its pitch circle is drawn just inside its tips.
        for (int i = 0; i < spinners.Count; i++)
        {
            var s = spinners[i];
            if (s.pitch > 0f) continue;
            float thinnest = s.radius;
            for (int j = 0; j < spinners.Count; j++)
                if (Vector3.Cross(spinners[j].axisHolder, s.axisHolder).sqrMagnitude < 1e-4f &&
                    Vector3.ProjectOnPlane(spinners[j].originHolder - s.originHolder, s.axisHolder).sqrMagnitude < 1e-5f)
                    thinnest = Mathf.Min(thinnest, spinners[j].radius);
            if (s.radius > 1.6f * thinnest && kind == Spin) s.pitch = s.radius * 0.93f;
            spinners[i] = s;
        }
        // The smallest number of reference turns after which every part is back at its starting angle, if small.
        for (int k = 1; k <= 4 && symmetry == 0; k++)
        {
            bool whole = true;
            for (int i = 0; i < spinners.Count && whole; i++)
                whole = Mathf.Abs(k * spinners[i].ratio - Mathf.Round(k * spinners[i].ratio)) < 0.02f;
            if (whole) symmetry = k;
        }
    }

    // Slow evenly to a stop over about `seconds`, landing every part on its starting angle when that's close enough
    // (props), so nothing jumps when the demo puts them back.
    void BeginSpinDown(float seconds)
    {
        stopping = true;
        stopStart = clock;
        stopFrom = phi;
        stopShare = speed;
        stopSpeed = referenceSpeed * speed;
        float pace = Mathf.Abs(stopSpeed);
        stopSeconds = seconds;
        stopDelta = stopSpeed * seconds * 0.5f;
        if (pace < 1f)
        {
            stopSeconds = 0.01f;
            stopDelta = 0f;
            return;
        }
        if (symmetry > 0)
        {
            float period = 360f * symmetry, least = pace * seconds * 0.5f;
            float target = stopSpeed > 0f ? Mathf.Ceil((phi + least) / period) * period : Mathf.Floor((phi - least) / period) * period;
            float exact = 2f * Mathf.Abs(target - phi) / pace;
            if (exact <= seconds * 2.2f)
            {
                stopSeconds = exact;
                stopDelta = target - phi;
            }
        }
    }

    // Turn the spinners; true once a spin-down has come to rest.
    bool Turn(float dt)
    {
        bool rested = false;
        if (!stopping) phi += referenceSpeed * speed * dt;
        else
        {
            float t = clock - stopStart;
            if (t >= stopSeconds)
            {
                phi = stopFrom + stopDelta;
                speed = 0f;
                rested = true;
            }
            else
            {
                phi = stopFrom + stopSpeed * t - 0.5f * stopSpeed / stopSeconds * t * t;
                speed = stopShare * (1f - t / stopSeconds);
            }
        }
        for (int i = 0; i < spinners.Count; i++)
        {
            var s = spinners[i];
            if (s.transform == null) continue;
            Quaternion q = Quaternion.AngleAxis(Mathf.Repeat(phi * s.ratio, 360f), s.axis);
            s.transform.localPosition = s.origin + q * (s.restPosition - s.origin);
            s.transform.localRotation = q * s.restRotation;
        }
        return rested;
    }
}
