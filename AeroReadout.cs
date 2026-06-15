using System.Collections.Generic;
using System.Reflection;
using Assets.Scripts.Flight;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using TMPro;

namespace VesselSwitcher
{
    // Forwards drag (pan) and scroll (zoom) on the graph image to the readout.
    public class GraphDragInput : MonoBehaviour, IDragHandler, IScrollHandler
    {
        public System.Action<Vector2> onDrag;
        public System.Action<float> onScroll;
        public void OnDrag(PointerEventData e){ onDrag?.Invoke(e.delta); }
        public void OnScroll(PointerEventData e){ onScroll?.Invoke(e.scrollDelta.y); }
    }

    public class AeroReadout : MonoBehaviour
    {
        // ── Injection ─────────────────────────────────────────────────────────
        private bool   _injected;
        private float  _tryTimer;
        private bool   _panelVisible;
        private GameObject _aeroSection;
        private GameObject _toggleBtn;      // our injected Aero button (watch for destruction)
        private bool   _diagDumped;
        private bool   _soundTried;

        // ── Native style template (copied from an SP2 label) ──────────────────
        private TMP_FontAsset _font;
        private Material      _fontMat;
        private float         _fontSize = 14f;
        private Color         _fontColor = new Color(0.9f, 0.92f, 0.96f);

        // ── Wing data ─────────────────────────────────────────────────────────
        private struct WingSnap
        {
            public string name;
            public float cl, cd, liftN, dragN, aoa;
            public float area, span, chord, thickness, camber, camberPos;
            public float liftGradient, stallPos, stallNeg, cd0;
            public bool  hasAero;
            public bool  fromSp2Model;   // true → liftGradient/cd0 came from SP2's PrecomputedLift (flight)
        }
        private List<WingSnap> _wings = new List<WingSnap>();
        private WingSnap _rep;          // representative (largest-area) wing for all aero curves
        private float _totalAreaAll;    // sum of every wing piece (whole aircraft)
        private bool  _aeroScopeAll;    // false = "Wing" (selected + its mirror); true = "All" lifting surfaces
        private float _scopeArea;       // cached effective lifting area for the current scope (computed per refresh)
        // The CL curve is always the selected wing's (already the full spanwise wing incl. all sections); scope
        // only rescales the area-dependent numbers (total lift, Vstall, takeoff).
        private float LiftArea(WingSnap w) => _scopeArea > 0.05f ? _scopeArea : w.area;
        // "Wing" = the whole connected wing structure containing the selected part (flood across wing-to-wing
        //          connections — captures every span segment whichever one you select) + each segment's symmetric
        //          mirror (paired by equal area). "All" = every horizontal (lift-producing) wing on the craft.
        private float ComputeScopeArea()
        {
            try {
                if (_wsType==null || _wsDataProp==null) return _rep.area;
                var f = BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance;
                var psProp = _wsType.GetProperty("PartScript", f);
                object selData = GetToolWing(out _);

                var infos = new System.Collections.Generic.List<(object part, float area, float vert)>();
                var partToIdx = new System.Collections.Generic.Dictionary<object,int>();
                int selIdx=-1;
                foreach (var mb in Object.FindObjectsByType<MonoBehaviour>(FindObjectsSortMode.None))
                {
                    if (mb==null || mb.GetType()!=_wsType) continue;
                    object data=null; try { data=_wsDataProp.GetValue(mb); } catch {}
                    if (data==null) continue;
                    float area=PF(data,_jdArea2); if (area<=0.001f) continue;
                    float vert=1f; try { vert=Mathf.Abs(Vector3.Dot(mb.transform.up, Vector3.up)); } catch {}
                    object part=null; try { object ps=psProp?.GetValue(mb); part=ps?.GetType().GetProperty("Part")?.GetValue(ps); } catch {}
                    if (ReferenceEquals(data, selData)) selIdx = infos.Count;
                    if (part!=null && !partToIdx.ContainsKey(part)) partToIdx[part]=infos.Count;
                    infos.Add((part, area, vert));
                }
                if (infos.Count==0) return _rep.area;

                if (_aeroScopeAll)
                {
                    float s=0f; foreach (var w in infos) if (w.vert>0.5f) s+=w.area;
                    return s>0.05f ? s : _totalAreaAll;
                }

                if (selIdx<0) selIdx=0;
                // flood across wing-to-wing connections from the selected segment
                var visited = new System.Collections.Generic.HashSet<int>();
                var queue = new System.Collections.Generic.Queue<int>(); queue.Enqueue(selIdx);
                int guard=0;
                while (queue.Count>0 && guard++<500)
                {
                    int i=queue.Dequeue(); if (!visited.Add(i)) continue;
                    object part=infos[i].part; if (part==null) continue;
                    try {
                        var conns = part.GetType().GetProperty("PartConnections",f)?.GetValue(part) as System.Collections.IEnumerable;
                        if (conns!=null) foreach (var c in conns)
                        {
                            object other=null; try { other=c.GetType().GetMethod("GetOtherPart")?.Invoke(c, new object[]{part}); } catch {}
                            if (other!=null && partToIdx.TryGetValue(other, out int oi) && !visited.Contains(oi)) queue.Enqueue(oi);
                        }
                    } catch {}
                }
                // chain + each segment's symmetric mirror (an unused JWing of equal area)
                float total=0f; var used=new System.Collections.Generic.HashSet<int>();
                foreach (int i in visited) { total+=infos[i].area; used.Add(i); }
                foreach (int i in new System.Collections.Generic.List<int>(visited))
                {
                    float a=infos[i].area;
                    for (int j=0;j<infos.Count;j++)
                    {
                        if (used.Contains(j)) continue;
                        if (Mathf.Abs(infos[j].area-a) <= 0.03f*a+0.05f) { total+=infos[j].area; used.Add(j); break; }
                    }
                }
                return total>0.05f ? total : _rep.area;
            } catch { return _rep.area; }
        }
        private float _speedMs, _altM, _dynPa, _totalLiftN, _totalDragN;
        private bool  _inFlight;

        // ── History buffers ───────────────────────────────────────────────────

        // ── Axis selection (computed airfoil curves) ──────────────────────────
        private enum XAx { AoA, Speed, CD }
        private enum YAx { CL, CD, LD, Lift, CM }
        private static readonly string[] XNames = { "AoA", "Speed", "CD" };
        private static readonly string[] YNames = { "CL", "CD", "L/D", "Lift", "CM" };
        private static readonly string[] XUnits = { "°", "kts", "" };
        private static readonly string[] YUnits = { "", "", "", "kN", "" };
        private XAx _xAxis = XAx.AoA;
        private YAx _yAxis = YAx.CL;
        private TextMeshProUGUI _xBtnLabel, _yBtnLabel, _scaleLabel;

        // Test conditions for force/speed plots (designer has no live airflow)
        private float _testSpeedMs = 50f;   // ~97 kts, used when computing Lift vs AoA
        private float _testAoADeg  = 5f;    // used when sweeping Speed
        private GameObject _speedStepperRow, _aoaStepperRow, _flapStepperRow, _slatStepperRow;

        // On-graph tick labels (pooled — one per gridline step, grown on demand)
        private Transform _gcT;
        private readonly List<TextMeshProUGUI> _xTickPool = new List<TextMeshProUGUI>();
        private readonly List<TextMeshProUGUI> _yTickPool = new List<TextMeshProUGUI>();

        // ── Graph ─────────────────────────────────────────────────────────────
        private const int GW = 268, GH = 230;   // plot width kept ≤ flyout content so the right edge/tick isn't clipped
        private const int ML = 42, MB = 20;   // graph margins: left (Y ticks), bottom (X ticks)
        // Pan/zoom view window (MATLAB-style). When _viewCustom, these override the auto bounds.
        private bool _viewCustom; private float _vxMin,_vxMax,_vyMin,_vyMax;
        private Texture2D _graphTex; private Color[] _graphBg;
        private RawImage  _graphImage;
        private TextMeshProUGUI _statsText, _axisLabel;
        private LayoutElement _statsLE;
        private TextMeshProUGUI _verifyText;
        private string _verifySummary = "";
        private string _lastAutoVerifyKey = "";
        private bool _autoVerifyPending;

        // ── Reflection: WingPhysicsScript (flight) ────────────────────────────
        private System.Type  _wpsType;
        private PropertyInfo _pCL,_pCD,_pLiftN,_pDragN,_pAoA,_pArea,_pSpan,_pChord,_pThick,_pCamber,_pPre;
        private System.Type  _preType; private FieldInfo _fLG,_fSP,_fSN,_fCD0;

        // ── Reflection: designer wing (JWingScript.Data → JWingData) ──────────
        private System.Type  _wsType;
        private PropertyInfo _wsDataProp;        // JWingScript.Data
        private PropertyInfo _wsPhysicsProp;     // JWingScript.Physics → WingPhysicsManager (live)
        private PropertyInfo _jdArea, _jdSpan, _jdMass; // on JWingData

        // ── Reflection: JWingTool (the designer's selected wing/slice) ────────
        // JWingTool is NOT a MonoBehaviour — reach it via Designer.Instance.Tools.JWingTool
        private PropertyInfo _designerInstanceProp, _toolsProp, _jwingToolProp;
        private PropertyInfo _dAircraftProp, _acComProp, _comLoadedMassProp;   // craft total mass chain
        private float _testMassKg;   // takeoff-weight parameter (0 ⇒ use the craft's real loaded mass)
        private System.Type  _toolType;
        private PropertyInfo _tCurWing, _tSliceAirfoil;          // JWingTool.CurrentWing, .SliceAirfoil
        private PropertyInfo _jdArea2,_jdSpan2,_jdLiftScale,_jdViscScale,_jdZldScale,_jdSlices; // on JWingData
        private PropertyInfo _iwsAirfoil;                        // InputWingSlice.Airfoil
        private string _airfoilName = "";                        // NACA designation of the analysed slice
        private float _liftScale=1f, _viscScale=1f, _zldScale=1f;
        private static readonly System.Text.RegularExpressions.Regex _nacaRx =
            new System.Text.RegularExpressions.Regex(@"NACA\s*(\d)(\d)(\d\d)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        // ── Sound ─────────────────────────────────────────────────────────────
        private MethodInfo _playSound;           // AudioManager.PlaySound overload
        private bool       _playSoundHasPitch;   // true if 5-arg (volume/delay/pitch) overload
        private object     _clickAudioFile;      // a UI AudioFile to play on click

        private bool _cacheReady; private int _cacheTries; private float _sampleTimer, _headlessVerifyTimer; private bool _headlessVerifyDone;

        // ─────────────────────────────────────────────────────────────────────
        private void Start()
        {
            _graphTex = new Texture2D(GW, GH, TextureFormat.RGBA32, false);
            _graphBg  = Fill(GW*GH, new Color(0.05f, 0.06f, 0.09f));
            SceneManager.sceneLoaded += (s, m) => ResetState();
        }

        private void ResetState()
        {
            _injected=false; _aeroSection=null; _toggleBtn=null; _panelVisible=false; _diagDumped=false; _soundTried=false;
            _showDeflect=false; _deflectActive=false; _deflectDrivers.Clear();
            _hiliteMats.Clear(); _deflectRest.Clear(); _meshDeflectActive=false; _lastDeflectFlap=-999f; _lastDeflectSlat=-999f;
            _cacheReady=false; _cacheTries=0; _wings.Clear();
            _font=null; _fontMat=null; _wsType=null; _wpsType=null;
            _graphImage=null; _statsText=null; _verifyText=null; _xBtnLabel=null; _yBtnLabel=null;
            _verifySummary=""; _lastAutoVerifyKey=""; _autoVerifyPending=false;
            _quickFlightStarted=false; _quickFlightDone=false; _quickFlightLifted=false; _quickCaseIndex=0; _quickSettleFrames=0; _quickSamples=0; _quickTotalSamples=0;
            _quickCaseClAbs=_quickCaseCdAbs=_quickCaseClMax=_quickCaseCdMax=_quickAllClAbs=_quickAllCdAbs=_quickAllClMax=_quickAllCdMax=0f;
        }

        private void Update()
        {
          try {   // defensive: never let one bad frame break the panel
            // Re-inject if SP2 destroyed our button (wing editor closed & reopened)
            if (_injected && _toggleBtn == null) { _injected=false; _panelVisible=false; }

            if (!_injected){ _tryTimer+=Time.deltaTime; if(_tryTimer>=0.5f){_tryTimer=0;TryInject();} }
            if (!_cacheReady && _cacheTries<60) TryBuildCache();

            // Test-harness mode is OFF unless the sentinel flag exists. In normal play NONE of the
            // verification machinery runs — the mod is just the Aero panel / lift graphs.
            _harnessTimer += Time.deltaTime;
            if (_harnessTimer > 1f)
            {
                _harnessTimer = 0f; _harnessMode = System.IO.File.Exists(HarnessFlagPath);
                if (_harnessMode) { try { string fc=System.IO.File.ReadAllText(HarnessFlagPath); _stEnabled=fc.Contains("selftest"); _stQuit=fc.Contains("quit"); } catch {} }
                else _stEnabled=false;
            }
            if (_stEnabled) SelfTestUpdate();   // full automated test gauntlet (supersedes the old auto-launch)

            if (_panelVisible)
            {
                _sampleTimer+=Time.deltaTime;
                if(_sampleTimer>=0.1f){_sampleTimer=0;RefreshData();MaybeSweep();RedrawGraph();UpdateStats();RefreshLiveLabels();BuildDeflectReadout(); if(_harnessMode) MaybeRunAutoVerify();}
            }
            else if (_harnessMode && !_stEnabled && !_headlessVerifyDone && _cacheReady && !IsInFlight())
            {
                // Run the editor baseline verify ONCE (not on a repeating timer): each pass builds ~135
                // probe wings, and looping it kept native/solver state churning right up to flight load,
                // which stalled the craft-load determinism pass ("Got consistent result"). One shot only.
                _headlessVerifyTimer += Time.deltaTime;
                if (_headlessVerifyTimer >= 2f) { _headlessVerifyDone = true; RefreshData(); RunAutoVerify(force:false); }
            }

            // Flight verification harness — ONLY in harness mode (never hijacks a normal flight).
            // NOTE: LogFlightVerify removed from the loop — it built a probe wing every 0.5s for the WHOLE
            // flight (including the craft-load/determinism window), which stalled the load. The [AeroQuick]
            // samples inside RunQuickFlightTest already do the live-vs-probe comparison, gated by a settle.
            if (_harnessMode && _wsType!=null && _wsPhysicsProp!=null && IsInFlight())
                RunQuickFlightTest();

            if (_harnessMode && !_stEnabled) MaybeAutoLaunchTest();   // self-test drives its own flight launch

            // Surface identification (editor only): when "Surfaces" is ON, outline every control surface the
            // calculator picked up — using SP2's own native part highlight — so the user can visually confirm
            // the right parts are included on ANY craft. Legacy wings also get their Angle driven for a free
            // visual deploy; JWings can't (their hinges are a Burst job that doesn't run in the editor), but
            // the highlight + per-surface readout make exactly what's included unambiguous.
            bool wantHi = _showDeflect && _panelVisible && !_inFlight;
            if (wantHi)
            {
                _deflectRefresh += Time.deltaTime;
                if (_deflectDrivers.Count==0 || _deflectRefresh>1f) { _deflectRefresh=0f; RefreshDeflectionDrivers(); }
                DriveDeflections();      // readout data + legacy-wing Angle deploy
                ApplyHilite(true);       // outline the surface parts (idempotent)
                _deflectActive=true;
                // Faithful JWing mesh deflection: recompute when Test-Flap/Test-Slat changes — but DEBOUNCED
                // to fire after the drag pauses (each rebuild = a probe+solve per wing; doing it every tick
                // while dragging tanked the framerate).
                _deflectRebuildTimer += Time.deltaTime;
                bool deflChanged = Mathf.Abs(_flapDeflect-_lastDeflectFlap) > 0.005f || Mathf.Abs(_slatDeflect-_lastDeflectSlat) > 0.005f;
                if (deflChanged && Time.unscaledTime-_sliderChangeAt > 0.25f && _deflectRebuildTimer > 0.15f)
                { _deflectRebuildTimer=0f; _lastDeflectFlap=_flapDeflect; _lastDeflectSlat=_slatDeflect; RebuildMeshDeflection(); _meshDeflectActive=true; }
            }
            else if (_deflectActive)
            {
                ResetDeflections();
                ApplyHilite(false);      // remove the outline
                if (_meshDeflectActive) { RestoreMeshDeflection(); _meshDeflectActive=false; _lastDeflectFlap=-999f; _lastDeflectSlat=-999f; }
                _deflectActive=false;
            }
          }
          catch (System.Exception e)
          {
              // Defensive (one bad frame must not kill the panel) but NOT silent — surface the first few
              // exceptions so a stalled readout can be diagnosed.
              if (_updErrCount < 5)
              {
                  _updErrCount++;
                  Debug.LogWarning("[Aero] Update EXC: " + e);
                  try { System.IO.File.AppendAllText(@"E:\Temp\aero_err.txt", System.DateTime.Now.ToString("HH:mm:ss ") + e + "\n\n"); } catch {}
              }
          }
        }
        private int _updErrCount;
        private bool _harnessMode; private float _harnessTimer;
        // Dev-only verification harness gate. The mod is inert unless this flag file exists. It lives
        // under BepInEx\config so there is NO hardcoded absolute path in the shipped binary, and players
        // never create it — so the autopilot/auto-launch machinery below stays completely dormant.
        private static string HarnessFlagPath => System.IO.Path.Combine(BepInEx.Paths.ConfigPath, "sp2_aero_harness.flag");

        // Sentinel-gated full-auto: when the harness flag exists, launch the editor craft straight into
        // flight (SceneManager.LoadFlight) so the [AeroQuick] verification rig runs hands-off.
        private bool _autoTestFired; private float _autoTestTimer;
        private void MaybeAutoLaunchTest()
        {
            if (_autoTestFired) return;
            if (!System.IO.File.Exists(HarnessFlagPath)) return;
            if (IsInFlight()) { _autoTestFired = true; return; }  // already flying
            var gameType = FindType("Assets.Scripts.Game");
            object game = gameType?.GetProperty("Instance", BindingFlags.Public|BindingFlags.Static|BindingFlags.NonPublic)?.GetValue(null);
            object sm = game?.GetType().GetProperty("SceneManager")?.GetValue(game);
            if (sm==null) return;
            bool inMenu=false, inFlight=false, inDesigner=false; string scene="?";
            try{ scene=(string)(sm.GetType().GetProperty("CurrentScene")?.GetValue(sm) ?? "?"); }catch{}
            try{ inMenu=(bool)(sm.GetType().GetProperty("InMenuScene")?.GetValue(sm) ?? false); }catch{}
            try{ inFlight=(bool)(sm.GetType().GetProperty("InFlightScene")?.GetValue(sm) ?? false); }catch{}
            try{ inDesigner=(bool)(sm.GetType().GetProperty("InDesignerScene")?.GetValue(sm) ?? false); }catch{}
            if (inFlight) { _autoTestFired=true; return; }
            // SP2 boots resuming the Designer (InMenuScene=false there); allow launch from menu OR designer.
            if (!(inMenu || inDesigner)) return;
            _autoTestTimer += Time.deltaTime;
            if (_autoTestTimer < 10f) return;             // let the menu settle
            _autoTestFired = true;
            Debug.Log("[AeroAuto] AUTO-LAUNCH: loading editor craft into flight for [AeroQuick] verification");
            try {
                var lf = sm.GetType().GetMethod("LoadFlight", new[]{ typeof(string), typeof(string) });
                lf?.Invoke(sm, new object[]{ null, "__editor__.xml" });
            } catch (System.Exception e){ Debug.Log("[AeroAuto] LoadFlight EXC "+e); }
        }

        // ═══════════════════════ FULL AUTOMATED SELF-TEST ═══════════════════════
        // Gated by the harness flag CONTAINING "selftest" (add "quit" to auto-exit when done).
        // Runs the entire gauntlet hands-off: open wing editor → UI checks → aero/solver checks →
        // mesh-deflection checks → launch flight → in-flight checks → return to designer →
        // regression checks. Writes PASS/FAIL report to E:\Temp\aero_selftest.txt as it goes.
        private bool _stEnabled, _stQuit, _stNavTried;
        private int _stState = 0; private float _stTimer, _stWait;
        private readonly System.Collections.Generic.List<string> _stResults = new System.Collections.Generic.List<string>();
        private static string SelfTestReportPath => @"E:\Temp\aero_selftest.txt";
        private void StRec(string id, bool pass, string detail)
        {
            string line = $"{(pass ? "PASS" : "FAIL")}  {id}  {detail}";
            _stResults.Add(line); Debug.Log("[AeroSelfTest] " + line);
        }
        private void StSkip(string id, string why) { _stResults.Add($"SKIP  {id}  {why}"); }
        private void StWrite(bool final)
        {
            try {
                var sb=new System.Text.StringBuilder();
                sb.AppendLine($"AERO SELF-TEST {(final ? "COMPLETE" : "in progress")}  state={_stState}  t={Time.realtimeSinceStartup:F0}s");
                int p=0,fc=0; foreach (var r in _stResults){ if(r.StartsWith("PASS"))p++; else if(r.StartsWith("FAIL"))fc++; }
                sb.AppendLine($"{p} passed, {fc} failed, {_stResults.Count-p-fc} skipped");
                foreach (var r in _stResults) sb.AppendLine(r);
                System.IO.File.WriteAllText(SelfTestReportPath, sb.ToString());
            } catch {}
        }
        private bool StInDesigner()
        {
            try {
                if (_gameTypeCache==null) _gameTypeCache = FindType("Assets.Scripts.Game");
                object game = _gameTypeCache?.GetProperty("Instance", BindingFlags.Public|BindingFlags.Static|BindingFlags.NonPublic)?.GetValue(null);
                object sm = game?.GetType().GetProperty("SceneManager")?.GetValue(game);
                return (bool)(sm?.GetType().GetProperty("InDesignerScene")?.GetValue(sm) ?? false);
            } catch { return false; }
        }
        // Select the largest JWing part and open SP2's wing editor on it (what the user does by hand).
        private bool StOpenWingEditor()
        {
            try {
                var designerT = FindType("Assets.Scripts.Design.Designer");
                object designer = designerT?.GetProperty("Instance", BindingFlags.Public|BindingFlags.Static)?.GetValue(null);
                if (designer==null) return false;
                object bestPs=null; float bestArea=-1f;
                foreach (var mb in Object.FindObjectsByType<MonoBehaviour>(FindObjectsSortMode.None))
                {
                    if (mb==null || mb.GetType()!=_wsType) continue;
                    object data=null; try { data=_wsDataProp.GetValue(mb); } catch {}
                    if (data==null) continue;
                    float a=PF(data,_jdArea2);
                    if (a>bestArea) { bestArea=a; bestPs=_wsType.GetProperty("PartScript")?.GetValue(mb); }
                }
                if (bestPs==null) return false;
                designerT.GetProperty("SelectedPart")?.SetValue(designer, bestPs);
                object tools = designerT.GetProperty("Tools")?.GetValue(designer);
                tools?.GetType().GetMethod("SelectJWingAdjustmentTool")?.Invoke(tools, null);
                return true;
            } catch (System.Exception e) { Debug.Log("[AeroSelfTest] open editor EXC "+e.Message); return false; }
        }
        private void StLoadScene(string method)
        {
            try {
                if (_gameTypeCache==null) _gameTypeCache = FindType("Assets.Scripts.Game");
                object game = _gameTypeCache?.GetProperty("Instance", BindingFlags.Public|BindingFlags.Static|BindingFlags.NonPublic)?.GetValue(null);
                object sm = game?.GetType().GetProperty("SceneManager")?.GetValue(game);
                if (method=="flight") sm?.GetType().GetMethod("LoadFlight", new[]{typeof(string),typeof(string)})?.Invoke(sm, new object[]{ null, "__editor__.xml" });
                else sm?.GetType().GetMethod("LoadDesigner")?.Invoke(sm, new object[]{ null });
            } catch (System.Exception e) { Debug.Log("[AeroSelfTest] load EXC "+e.Message); }
        }

        private void SelfTestUpdate()
        {
            _stTimer += Time.deltaTime; _stWait += Time.deltaTime;
            switch (_stState)
            {
                case 0:   // boot: wait for designer + type cache + a craft with JWings (self-navigate from the menu)
                    if (!StInDesigner() || !_cacheReady || _wsType==null)
                    {
                        if (!_stNavTried && _stWait>20f) { _stNavTried=true; Debug.Log("[AeroSelfTest] not in designer — navigating"); StLoadScene("designer"); }
                        if (_stWait>150f){ StRec("S0.boot",false,$"designer/cache not ready in 150s (designer={StInDesigner()} cache={_cacheReady})"); _stState=99; }
                        return;
                    }
                    bool anyWing=false;
                    foreach (var mb in Object.FindObjectsByType<MonoBehaviour>(FindObjectsSortMode.None)) if (mb!=null && mb.GetType()==_wsType){ anyWing=true; break; }
                    if (!anyWing) { if (_stWait>90f){ StRec("S0.boot",false,"no JWing parts found"); _stState=99; } return; }
                    StRec("S0.boot",true,"designer + cache + JWings present");
                    _stState=1; _stWait=0; break;

                case 1:   // open the wing editor programmatically, wait for our injection
                    if (_stWait<0.5f) return;
                    if (!_injected)
                    {
                        if (_stWait<1f) StOpenWingEditor();
                        if (_stWait>12f) { StRec("S1.inject",false,"wing editor/injection not up in 12s"); StWrite(false); _stState=4; _stWait=0; }
                        return;
                    }
                    StRec("S1.inject",true,"wing editor open, Aero button injected");
                    if (!_panelVisible && _toggleBtn!=null) _toggleBtn.GetComponent<Button>()?.onClick.Invoke();   // click Aero like a user
                    _stState=2; _stWait=0; break;

                case 2:   // UI checks (panel open)
                    if (_stWait<1.5f) return;
                    StRec("S2.panelOpen", _panelVisible && _aeroSection!=null && _aeroSection.activeSelf, "Aero panel visible after button click");
                    int blank=0, btns=0;
                    if (_aeroSection!=null) foreach (var b in _aeroSection.GetComponentsInChildren<Button>(true))
                    { btns++; var l=b.GetComponentInChildren<TextMeshProUGUI>(true); if (l==null || string.IsNullOrWhiteSpace(l.text)) blank++; }
                    StRec("S2.labels", btns>0 && blank==0, $"{btns} buttons, {blank} blank labels");
                    // sliders drive their backing values
                    var fs = FindGO("Test FlapSlider")?.GetComponent<UnityEngine.UI.Slider>();
                    if (fs!=null) { fs.value=0.37f; StRec("S2.flapSlider", Mathf.Abs(_flapDeflect-0.37f)<0.01f, $"slider→_flapDeflect={_flapDeflect:F2}"); fs.value=0f; }
                    else StRec("S2.flapSlider", false, "Test Flap slider not found");
                    var ss = FindGO("Test SlatSlider")?.GetComponent<UnityEngine.UI.Slider>();
                    if (ss!=null) { ss.value=0.41f; StRec("S2.slatSlider", Mathf.Abs(_slatDeflect-0.41f)<0.01f, $"slider→_slatDeflect={_slatDeflect:F2}"); ss.value=0f; }
                    else StRec("S2.slatSlider", false, "Test Slat slider not found");
                    // native sound hookup resolves
                    try {
                        object game = _gameTypeCache?.GetProperty("Instance", BindingFlags.Public|BindingFlags.Static|BindingFlags.NonPublic)?.GetValue(null);
                        object ui = game?.GetType().GetProperty("UserInterface")?.GetValue(game);
                        StRec("S2.sound", ui?.GetType().GetProperty("Sound")?.GetValue(ui)!=null, "UserInterface.Sound resolves");
                    } catch { StRec("S2.sound", false, "exception resolving sound player"); }
                    _stState=3; _stWait=0; break;

                case 3:   // aero + deflection checks (the heavy ones)
                    if (_stWait<0.5f) return;
                    try { RunAeroChecks(); } catch (System.Exception e) { StRec("S3.EXC", false, e.GetType().Name+": "+e.Message); }
                    StWrite(false);
                    _stState=4; _stWait=0; break;

                case 4:   // launch flight with the same craft
                    StLoadScene("flight"); _autoTestFired=true;
                    _stState=5; _stWait=0; break;

                case 5:   // wait for flight + live articulation
                    if (!IsInFlight()) { if (_stWait>150f){ StRec("F1.flight",false,"not in flight after 150s"); StWrite(false); _stState=7; _stWait=0; } return; }
                    int withInput=0, total=0;
                    try {
                        var inF = _wsType?.GetField("_input", BindingFlags.NonPublic|BindingFlags.Instance);
                        foreach (var mb in Object.FindObjectsByType<MonoBehaviour>(FindObjectsSortMode.None))
                        { if (mb==null || mb.GetType()!=_wsType) continue; total++; if (inF?.GetValue(mb)!=null) withInput++; }
                    } catch {}
                    if (withInput==0) { if (_stWait>150f){ StRec("F2.input",false,$"no live WingInputManager ({total} wings)"); StWrite(false); _stState=6; _stWait=0; } return; }
                    StRec("F1.flight",true,"flight scene loaded with craft");
                    StRec("F2.input",true,$"{withInput}/{total} wings articulated (live _input)");
                    _stState=6; _stWait=0; break;

                case 6:   // let the AeroQuick live-vs-probe rig run (slow: two decel-to-stall sweeps)
                    if (!_quickFlightDone) { if (_stWait>540f){ StRec("F3.aeroQuick",false,$"not done in 540s (case {_quickCaseIndex}, {_quickTotalSamples} samples so far)"); StWrite(false); _stState=7; _stWait=0; } return; }
                    StRec("F3.aeroQuick", _quickTotalSamples>0, $"live-vs-probe rig done, {_quickTotalSamples} samples (details in Player.log [AeroQuick])");
                    _stState=7; _stWait=0; break;

                case 7:   // return to the designer
                    StLoadScene("designer");
                    _stState=8; _stWait=0; break;

                case 8:   // regression checks after the round-trip
                    if (!StInDesigner()) { if (_stWait>90f){ StRec("R1.return",false,"not back in designer after 90s"); _stState=99; } return; }
                    if (_stWait<4f) return;                       // let it settle + cache rebuild
                    StRec("R1.return",true,"back in designer");
                    StRec("R2.inFlightFlag", !IsInFlight(), $"IsInFlight()={IsInFlight()} (stale-singleton regression)");
                    try { RefreshData(); StRec("R3.dataAlive", !_inFlight, $"_inFlight={_inFlight} after RefreshData (No-wing-physics regression)"); }
                    catch (System.Exception e) { StRec("R3.dataAlive", false, "RefreshData EXC "+e.Message); }
                    _stState=9; _stWait=0; break;

                case 9:   // re-open the wing editor — injection must survive the round-trip
                    if (!_injected)
                    {
                        if (_stWait<1f) StOpenWingEditor();
                        if (_stWait>15f) { StRec("R4.reinject",false,"no re-injection within 15s of reopening"); _stState=99; }
                        return;
                    }
                    StRec("R4.reinject",true,"Aero button re-injected after flight round-trip");
                    _stState=99; break;

                case 99:  // done
                    StWrite(true);
                    _stEnabled=false;
                    Debug.Log("[AeroSelfTest] DONE — report at "+SelfTestReportPath);
                    if (_stQuit) Application.Quit();
                    break;
            }
        }

        // Heavy aero checks: solver sanity, flap/slat aero response, mesh deflection + restore, scope, highlight.
        private void RunAeroChecks()
        {
            RefreshData();
            object wing = GetToolWing(out _) ?? GetLargestDesignerWing(out _);
            StRec("S3.wing", wing!=null, wing!=null ? $"selected wing found (area {_rep.area:F1} m²)" : "no wing data");
            if (wing==null) return;
            float savedF=_flapDeflect, savedS=_slatDeflect;
            try
            {
                // baseline sweep
                _flapDeflect=0f; _slatDeflect=0f; _probeWingOverride=null;
                RunSolverSweep(50f);
                StRec("S3.sweep", _solReady && _solAoA.Count>20, $"{_solAoA.Count} solver points");
                if (!_solReady) return;
                float slope=(InterpCurve(_solAoA,_solCL,2f)-InterpCurve(_solAoA,_solCL,-2f))/4f;
                float clMax=float.MinValue; foreach(var c in _solCL) if(c>clMax) clMax=c;
                float cl0=InterpCurve(_solAoA,_solCL,0f);
                StRec("S3.slope", slope>0.01f && slope<0.3f, $"lift slope {slope:F3}/°");
                StRec("S3.clmax", clMax>0.3f && clMax<6f, $"CLmax {clMax:F2}");
                StRec("S3.cd", InterpCurve(_solAoA,_solCD,0f)>0f, $"CD@0 {InterpCurve(_solAoA,_solCD,0f):F4}");

                // flap response: either slider sign must change CL@0 (crafts deploy on either sign)
                _flapDeflect=1f; RunSolverSweep(50f); float clP=_solReady?InterpCurve(_solAoA,_solCL,0f):cl0;
                _flapDeflect=-1f; RunSolverSweep(50f); float clM=_solReady?InterpCurve(_solAoA,_solCL,0f):cl0;
                bool flapResponds = Mathf.Abs(clP-cl0)>0.02f || Mathf.Abs(clM-cl0)>0.02f;
                StRec("S3.flapAero", flapResponds, $"CL@0: base {cl0:F2}, flap+1 {clP:F2}, flap-1 {clM:F2}");

                // slat response: CLmax should not DROP when slats deploy (and usually rises)
                _flapDeflect=0f; _slatDeflect=1f; RunSolverSweep(50f);
                if (_solReady){ float clMaxS=float.MinValue; foreach(var c in _solCL) if(c>clMaxS) clMaxS=c;
                    StRec("S3.slatAero", clMaxS>clMax-0.05f, $"CLmax {clMax:F2} → {clMaxS:F2} with slats"); }
                else StSkip("S3.slatAero","slat sweep failed");

                // mesh deflection: surfaces move at full deflection and restore exactly
                _flapDeflect=1f; _slatDeflect=1f;
                RebuildMeshDeflection();
                int moved=0;
                foreach (var e in _deflectRest)
                    if (e.t!=null && ((e.t.localPosition-e.p).sqrMagnitude>1e-8f || Quaternion.Angle(e.t.localRotation,e.r)>0.05f)) moved++;
                StRec("S3.meshMove", _surfCount==0 || moved>0, $"{moved}/{_deflectRest.Count} surface meshes deflected (n={_surfCount})");
                RestoreMeshDeflection();
                // re-run at neutral: deltas must be ~zero (restore correctness)
                _flapDeflect=0f; _slatDeflect=0f;
                RebuildMeshDeflection();
                int stray=0;
                foreach (var e in _deflectRest)
                    if (e.t!=null && ((e.t.localPosition-e.p).sqrMagnitude>1e-6f || Quaternion.Angle(e.t.localRotation,e.r)>0.5f)) stray++;
                StRec("S3.meshRestore", stray==0, $"{stray} surfaces away from rest at neutral");
                RestoreMeshDeflection();

                // scope areas: Wing ≥ selected, All ≥ Wing
                bool savedScope=_aeroScopeAll;
                _aeroScopeAll=false; float wA=ComputeScopeArea();
                _aeroScopeAll=true;  float aA=ComputeScopeArea();
                _aeroScopeAll=savedScope;
                StRec("S3.scope", wA>=_rep.area*0.99f && aA>=wA*0.99f, $"sel {_rep.area:F1} ≤ wing {wA:F1} ≤ all {aA:F1} m²");

                // highlight registry
                bool savedShow=_showDeflect; _showDeflect=true;
                RefreshDeflectionDrivers();
                StRec("S3.hilite", _hiliteMats.Count>0, $"{_hiliteMats.Count} surface parts registered for highlight");
                ApplyHilite(false); _showDeflect=savedShow;
            }
            finally { _flapDeflect=savedF; _slatDeflect=savedS; CleanupProbe(); }
        }

        // ── Injection ─────────────────────────────────────────────────────────
        private void TryInject()
        {
            var flyout = FindGO("flyout-wing-editor");
            if (flyout == null) return;

            CaptureNativeStyle(flyout);
            try { SetupSound(flyout); }
            catch (System.Exception e) { Debug.Log($"[AeroSound] setup skipped: {e.GetType().Name}: {e.Message}"); }

            // Find a native button to clone (Root / Tip)
            Button src = null;
            foreach (var b in flyout.GetComponentsInChildren<Button>(true))
            {
                var l = b.GetComponentInChildren<TextMeshProUGUI>(true);
                if (l != null && (l.text == "< Root" || l.text == "Tip >")) { src = b; break; }
            }
            if (src == null) return;

            var scroll = FindInChildren<ScrollRect>(flyout.transform);
            if (scroll == null) return;

            foreach (var t in flyout.GetComponentsInChildren<Transform>(true))
            {
                if (t != null && t.name == "AeroButton")
                {
                    Object.Destroy(t.gameObject);
                    break;
                }
            }

            // Toggle button (clone native → keeps sound/hover/scale/font)
            var toggle = CloneButton(src, "AeroButton", "Aero", src.transform.parent);
            var toggleButton = toggle.GetComponent<Button>();
            if (toggleButton == null) return;
            toggleButton.onClick.RemoveAllListeners();
            toggleButton.onClick.AddListener(TogglePanel);
            toggle.transform.SetAsLastSibling();
            _toggleBtn = toggle;

            BuildPanel(scroll.content, src);

            if (!_diagDumped) { DiagnosticDump(); _diagDumped = true; }
            _injected = true;
        }

        private void CaptureNativeStyle(GameObject flyout)
        {
            if (_font != null) return;
            // Prefer a slider/section label we know is native SP2
            foreach (var t in flyout.GetComponentsInChildren<TextMeshProUGUI>(true))
            {
                if (t.font == null) continue;
                if (t.text == "Thickness" || t.text == "Camber Height" || t.text == "STATISTICS" || t.text == "Wingspan")
                {
                    _font = t.font; _fontMat = t.fontSharedMaterial;
                    _fontSize = t.fontSize; _fontColor = t.color;
                    return;
                }
            }
            // Fallback: first TMP with a font
            foreach (var t in flyout.GetComponentsInChildren<TextMeshProUGUI>(true))
                if (t.font != null) { _font = t.font; _fontMat = t.fontSharedMaterial; _fontSize = t.fontSize; return; }
        }

        // Locate AudioManager.PlaySound + an AudioFile from a native ButtonWidget
        private void SetupSound(GameObject flyout)
        {
            if (_soundTried) return;
            _soundTried = true;
            var f = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

            // AudioManager.PlaySound(AudioFile, Nullable<Vector3>)
            foreach (var asm in System.AppDomain.CurrentDomain.GetAssemblies())
            {
                var at = asm.GetType("Assets.Scripts.Audio.AudioManager");
                if (at == null) continue;
                MethodInfo two=null, five=null;
                foreach (var m in at.GetMethods(BindingFlags.Public|BindingFlags.Static))
                {
                    if (m.Name != "PlaySound") continue;
                    var ps = m.GetParameters();
                    if (ps.Length>=1 && ps[0].ParameterType.Name != "AudioFile") continue;
                    if (ps.Length == 2) two = m;
                    // PlaySound(AudioFile, Nullable<Vector3>, float volume, float delay, float pitch)
                    if (ps.Length == 5 && ps[2].ParameterType==typeof(float)) five = m;
                }
                _playSound = five ?? two;     // prefer pitch-capable overload
                _playSoundHasPitch = five != null;
                break;
            }

            // Pull an AudioFile from any ButtonWidget in the panel (its click sound)
            bool dumped = false;
            foreach (var mb in flyout.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (mb == null || mb.GetType().Name != "ButtonWidget") continue;
                if (!dumped)
                {
                    Debug.Log($"[AeroSound] ButtonWidget fields:");
                    foreach (var fi in mb.GetType().GetFields(f))
                        Debug.Log($"[AeroSound]   {fi.FieldType.Name} {fi.Name} = {SafeVal(fi,mb)}");
                    dumped = true;
                }
                foreach (var fi in mb.GetType().GetFields(f))
                {
                    if (fi.FieldType.Name != "AudioFile" && fi.FieldType.Name != "AudioClip") continue;
                    var val = fi.GetValue(mb);
                    if (val != null) { _clickAudioFile = val; break; }
                }
                if (_clickAudioFile != null) break;
            }

            // Fallback: enumerate every loaded AudioFile, pick a UI-ish one by name
            if (_clickAudioFile == null)
            {
                System.Type aft = null;
                foreach (var asm in System.AppDomain.CurrentDomain.GetAssemblies())
                { aft = asm.GetType("Assets.Scripts.Audio.AudioFile"); if (aft != null) break; }
                if (aft != null)
                {
                    var all = Resources.FindObjectsOfTypeAll(aft);
                    Debug.Log($"[AeroSound] Found {all.Length} AudioFile objects");
                    // log names + pick best UI match
                    object best=null, firstAny=null;
                    string[] prefer = {"click","button","knob","tick","select","tap","ui","menu","beep"};
                    foreach (var o in all)
                    {
                        if (o == null) continue;
                        if (firstAny==null) firstAny=o;
                        string objName = o is UnityEngine.Object uo ? uo.name : o.ToString();
                        var nm = objName.ToLower();
                        Debug.Log($"[AeroSound]   AudioFile '{objName}'");
                        foreach (var key in prefer) if (nm.Contains(key)) { best=o; break; }
                        if (best!=null) break;
                    }
                    _clickAudioFile = best ?? firstAny;
                }
            }

            string clickName = "none";
            try { if (_clickAudioFile is UnityEngine.Object uo) clickName = uo.name; else if (_clickAudioFile != null) clickName = _clickAudioFile.ToString(); } catch {}
            Debug.Log($"[AeroSound] playSound={(_playSound!=null)} clickFile={(_clickAudioFile!=null)} file={clickName}");
        }

        private static string SafeVal(FieldInfo fi, object o){ try{var v=fi.GetValue(o);return v==null?"null":v.ToString();}catch{return "?";} }

        // ── Native SP2 UI sounds (Game.Instance.UserInterface.Sound.PlaySound) ──────────────
        // Our cloned/built widgets aren't registered with SP2's Juicy widget context, so they don't get the
        // automatic hover/click sounds — we fire the same UISound the game uses, explicitly.
        private System.Type _uiSndEnum;
        private void PlayUiSound(string name)
        {
            try {
                if (_gameTypeCache==null) _gameTypeCache = FindType("Assets.Scripts.Game");
                object game = _gameTypeCache?.GetProperty("Instance", BindingFlags.Public|BindingFlags.Static|BindingFlags.NonPublic)?.GetValue(null);
                object ui = game?.GetType().GetProperty("UserInterface")?.GetValue(game);
                object snd = ui?.GetType().GetProperty("Sound")?.GetValue(ui);
                if (snd==null) return;
                if (_uiSndEnum==null) _uiSndEnum = FindType("Assets.Scripts.UI.UISound");
                if (_uiSndEnum==null) return;
                object v = System.Enum.Parse(_uiSndEnum, name);
                snd.GetType().GetMethod("PlaySound").Invoke(snd, new object[]{ v, 1f });
            } catch {}
        }
        private void PlayClick() => PlayUiSound("ButtonClick");

        // Attach SP2's native hover feel on pointer-enter: the Hover sound + a subtle scale pop
        // (what cloned buttons miss because they aren't bound to the Juicy widget framework).
        private void AddHover(GameObject go)
        {
            try {
                var trig = go.GetComponent<UnityEngine.EventSystems.EventTrigger>() ?? go.AddComponent<UnityEngine.EventSystems.EventTrigger>();
                var tr = go.transform;
                var enter = new UnityEngine.EventSystems.EventTrigger.Entry { eventID = UnityEngine.EventSystems.EventTriggerType.PointerEnter };
                enter.callback.AddListener(_ => { PlayUiSound("Hover"); try { tr.localScale = new Vector3(1.05f, 1.05f, 1f); } catch {} });
                trig.triggers.Add(enter);
                var exit = new UnityEngine.EventSystems.EventTrigger.Entry { eventID = UnityEngine.EventSystems.EventTriggerType.PointerExit };
                exit.callback.AddListener(_ => { try { tr.localScale = Vector3.one; } catch {} });
                trig.triggers.Add(exit);
            } catch {}
        }

        private Sprite _btnSprite;   // captured from the native button template → native-looking slider track/handle
        private void SetBtnLayout(GameObject go, float prefW=100f)
        {
            var le = go.GetComponent<LayoutElement>() ?? go.AddComponent<LayoutElement>();
            le.preferredWidth=prefW; le.flexibleWidth=1; le.preferredHeight=28; le.minWidth=56;
        }

        private GameObject CloneButton(Button src, string name, string label, Transform parent)
        {
            var go = Instantiate(src.gameObject, parent);
            go.name = name;
            var lbl = go.GetComponentInChildren<TextMeshProUGUI>(true);
            if (lbl != null) { lbl.text = label; lbl.enabled = true;
                var c = lbl.color; if (c.a < 0.95f) c.a = 1f; lbl.color = c;   // un-grey a cloned label
                if (_font != null) lbl.font = _font;
                // long labels ("Surfaces: OFF") truncate in a 3-button row — shrink-to-fit instead
                lbl.enableWordWrapping = false;
                lbl.enableAutoSizing = true; lbl.fontSizeMin = 7f; lbl.fontSizeMax = Mathf.Max(10f, lbl.fontSize); }
            // The native source button (e.g. "< Root") may be in a disabled/greyed state when cloned —
            // force OUR button fully interactable so it isn't greyed out.
            var btn = go.GetComponent<Button>();
            if (btn != null) btn.interactable = true;
            var cg = go.GetComponent<CanvasGroup>();
            if (cg != null) { cg.interactable = true; cg.alpha = 1f; cg.blocksRaycasts = true; }
            AddHover(go);   // native hover sound (clones aren't bound to the Juicy widget context)
            // Visible hover highlight: the native hover effect lives in SP2's widget framework which clones
            // don't inherit — recreate it with a Unity ColorTint on the button image (brightens on hover).
            if (btn != null)
            {
                var img = btn.targetGraphic as UnityEngine.UI.Image ?? go.GetComponent<UnityEngine.UI.Image>() ?? go.GetComponentInChildren<UnityEngine.UI.Image>();
                if (img != null)
                {
                    btn.targetGraphic = img;
                    btn.transition = UnityEngine.UI.Selectable.Transition.ColorTint;
                    // Rest must look EXACTLY native (tint×multiplier = 1), hover must go BRIGHTER than rest
                    // like SP2's own buttons — needs colorMultiplier > 1 since a tint can't exceed 1 alone.
                    var cb = btn.colors;
                    cb.colorMultiplier = 1.3f;
                    cb.normalColor = new Color(1f/1.3f, 1f/1.3f, 1f/1.3f, 1f);   // ×1.3 → exactly native at rest
                    cb.highlightedColor = Color.white;                            // ×1.3 → 30% brighter on hover
                    cb.pressedColor = new Color(0.62f, 0.62f, 0.62f, 1f);
                    cb.selectedColor = new Color(1f/1.3f, 1f/1.3f, 1f/1.3f, 1f);
                    cb.fadeDuration = 0.08f;
                    btn.colors = cb;
                }
            }
            return go;
        }

        // ── Panel ─────────────────────────────────────────────────────────────
        private void BuildPanel(Transform parent, Button btnTemplate)
        {
            _aeroSection = new GameObject("AeroSection");
            _aeroSection.transform.SetParent(parent, false);
            var rt = _aeroSection.AddComponent<RectTransform>();
            // Top-stretch (NOT full-stretch): width follows parent, height is free so the
            // ContentSizeFitter below can drive it. Full-stretch + vertical CSF fight each
            // frame, which is what made the scroll view snap back / clip the stats.
            rt.anchorMin=new Vector2(0,1); rt.anchorMax=new Vector2(1,1); rt.pivot=new Vector2(0.5f,1f); rt.sizeDelta=Vector2.zero;

            var vlg = _aeroSection.AddComponent<VerticalLayoutGroup>();
            vlg.childForceExpandWidth=true; vlg.childForceExpandHeight=false;
            vlg.spacing=6; vlg.padding=new RectOffset(8,8,8,8);
            _aeroSection.AddComponent<ContentSizeFitter>().verticalFit=ContentSizeFitter.FitMode.PreferredSize;

            // Capture the native button's sprite so our slider can use the same rounded SP2 look.
            try { _btnSprite = (btnTemplate.targetGraphic as UnityEngine.UI.Image)?.sprite ?? btnTemplate.GetComponent<UnityEngine.UI.Image>()?.sprite; } catch {}

            // Axis cycle buttons (clone native buttons → sound/hover/font)
            var axisRow = MakeRow("AxisRow", _aeroSection.transform, 32);
            var xBtn = CloneButton(btnTemplate, "XAxisBtn", $"X: {XNames[(int)_xAxis]}", axisRow.transform);
            _xBtnLabel = xBtn.GetComponentInChildren<TextMeshProUGUI>(true);
            xBtn.GetComponent<Button>().onClick.AddListener(CycleX);
            var xle = xBtn.GetComponent<LayoutElement>() ?? xBtn.AddComponent<LayoutElement>();
            xle.preferredWidth=140; xle.flexibleWidth=1;

            var yBtn = CloneButton(btnTemplate, "YAxisBtn", $"Y: {YNames[(int)_yAxis]}", axisRow.transform);
            _yBtnLabel = yBtn.GetComponentInChildren<TextMeshProUGUI>(true);
            yBtn.GetComponent<Button>().onClick.AddListener(CycleY);
            var yle = yBtn.GetComponent<LayoutElement>() ?? yBtn.AddComponent<LayoutElement>();
            yle.preferredWidth=140; yle.flexibleWidth=1;

            // Test-condition sliders: the sweep holds one variable fixed — let the user drag it.
            // Speed fixes the airspeed for AoA/CD sweeps; AoA fixes incidence for Speed sweeps.
            _speedStepperRow = MakeSlider(_aeroSection.transform, "Test Speed", 1f, 264f,   // m/s (~2..513 kts)
                ()=>_testSpeedMs, v=>_testSpeedMs=v, ()=>$"{_testSpeedMs*1.944f:F0} kts");
            _aoaStepperRow = MakeSlider(_aeroSection.transform, "Test AoA", -20f, 30f,
                ()=>_testAoADeg, v=>_testAoADeg=v, ()=>$"{_testAoADeg:F1}°");
            // Flap/control-surface deflection (the real solver computes its effect, incl. funky-trees).
            // Flap-class surfaces follow this slider; slat-class surfaces follow the Test Slat slider below,
            // so they can be driven independently (or to the same value, to mirror "deploy together" crafts).
            _flapStepperRow = MakeSlider(_aeroSection.transform, "Test Flap", -1f, 1f,
                ()=>_flapDeflect, v=>_flapDeflect=v, ()=>$"{_flapDeflect*100f:F0} %");
            _slatStepperRow = MakeSlider(_aeroSection.transform, "Test Slat", -1f, 1f,
                ()=>_slatDeflect, v=>_slatDeflect=v, ()=>$"{_slatDeflect*100f:F0} %");
            // Takeoff weight: defaults to the craft's real loaded mass; draws a weight line + stall speed.
            MakeStepper(_aeroSection.transform, btnTemplate, "Mass",
                ()=>$"{EffMassKg:F0} kg", 50f,
                d=>{ float m=_testMassKg>0.5f?_testMassKg:GetCraftMass(); _testMassKg=Mathf.Clamp(m+d, 50f, 200000f); });
            UpdateStepperVisibility();

            _axisLabel = MakeText("CL vs AoA", _aeroSection.transform, 11f);
            _axisLabel.alignment = TextAlignmentOptions.Center;

            // Graph container with axis margins (ML left for Y ticks, MB bottom for X ticks)
            var gc = new GameObject("GraphContainer"); gc.transform.SetParent(_aeroSection.transform, false);
            var gcRT = gc.AddComponent<RectTransform>();
            var gcLE = gc.AddComponent<LayoutElement>(); gcLE.preferredHeight=GH+MB; gcLE.preferredWidth=GW+ML; gcLE.flexibleWidth=1;
            _gcT = gc.transform;

            // Plot image (inset by margins)
            var gGO = new GameObject("Graph"); gGO.transform.SetParent(gc.transform, false);
            _graphImage = gGO.AddComponent<RawImage>(); _graphImage.texture=_graphTex;
            var giRT = gGO.GetComponent<RectTransform>();
            giRT.anchorMin=new Vector2(0,1); giRT.anchorMax=new Vector2(0,1); giRT.pivot=new Vector2(0,1);
            giRT.anchoredPosition=new Vector2(ML,0); giRT.sizeDelta=new Vector2(GW,GH);
            _graphImage.raycastTarget = true;                       // receive drag-to-pan + scroll-to-zoom
            var gin = gGO.AddComponent<GraphDragInput>();
            gin.onDrag = PanGraph; gin.onScroll = s => ZoomGraph(s);
            // Tick labels are created lazily/pooled in DrawTicks (one per gridline step)

            // Graph nav: drag to pan, scroll-wheel to zoom; one button to reset back to auto-fit.
            // Wrap in a row (like the steppers) — a cloned button placed straight in the vertical
            // layout doesn't lay its text out, which is why the label was invisible.
            var ctrlRow = MakeRow("GraphCtrlRow", _aeroSection.transform, 32);
            // Native cloned buttons (same style, hover highlight + sounds). MUST give each a LayoutElement —
            // without it the cloned native label collapses to nothing in a 3-button row (the blank-text bug).
            var resetGo = CloneButton(btnTemplate, "ResetViewBtn", "Reset View", ctrlRow.transform);
            SetBtnLayout(resetGo);
            resetGo.GetComponent<Button>().onClick.AddListener(() => { PlayClick(); ResetView(); });
            // "Surfaces" toggle — highlights + deploys the craft's control surfaces for the current Test Flap/Slat.
            TextMeshProUGUI defLbl = null;
            var defGo = CloneButton(btnTemplate, "SurfacesBtn", "Surfaces: OFF", ctrlRow.transform);
            SetBtnLayout(defGo);
            defLbl = defGo.GetComponentInChildren<TextMeshProUGUI>(true);
            defGo.GetComponent<Button>().onClick.AddListener(
                () => { PlayClick(); _showDeflect = !_showDeflect; if (defLbl != null) defLbl.text = _showDeflect ? "Surfaces: ON" : "Surfaces: OFF"; });
            // Lift-area scope: the selected wing + its mirror, or every lifting surface (affects total lift /
            // Vstall, not the curve). Defaults to "Wing" — we never report just one unmirrored panel.
            TextMeshProUGUI scopeLbl = null;
            var scopeGo = CloneButton(btnTemplate, "ScopeBtn", "Scope: Wing", ctrlRow.transform);
            SetBtnLayout(scopeGo);
            scopeLbl = scopeGo.GetComponentInChildren<TextMeshProUGUI>(true);
            scopeGo.GetComponent<Button>().onClick.AddListener(
                () => { PlayClick(); _aeroScopeAll = !_aeroScopeAll; if (scopeLbl != null) scopeLbl.text = _aeroScopeAll ? "Scope: All" : "Scope: Wing"; });

            _scaleLabel = MakeText("", _aeroSection.transform, 9f);
            _scaleLabel.alignment = TextAlignmentOptions.Center;
            _scaleLabel.color = new Color(0.55f, 0.65f, 0.8f);

            _verifyText = MakeText("", _aeroSection.transform, _fontSize*0.66f);
            _verifyText.color = new Color(0.65f, 0.9f, 0.82f);

            _statsText = MakeText("…", _aeroSection.transform, _fontSize*0.72f);
            _statsLE = _statsText.gameObject.AddComponent<LayoutElement>(); _statsLE.preferredHeight=90; _statsLE.flexibleWidth=1;

            _aeroSection.SetActive(false);
        }

        // ◄ value ► stepper row in SP2's native style (mirrors the editor's Scale/Bend rows).
        // Returns the value label so the caller can keep a reference (it's updated on click).
        private GameObject MakeStepper(Transform parent, Button tmpl, string label,
            System.Func<string> fmt, float step, System.Action<float> add)
        {
            var row = MakeRow(label+"Row", parent, 30);
            var lbl = MakeText(label, row.transform, _fontSize*0.8f);
            lbl.alignment = TextAlignmentOptions.Left;
            (lbl.gameObject.AddComponent<LayoutElement>()).preferredWidth = 88;
            var dn = CloneButton(tmpl, label+"Dn", "◄", row.transform);
            { var le=dn.GetComponent<LayoutElement>() ?? dn.AddComponent<LayoutElement>(); le.preferredWidth=32; le.flexibleWidth=0; }
            var val = MakeText(fmt(), row.transform, _fontSize*0.8f);
            val.alignment = TextAlignmentOptions.Center;
            (val.gameObject.AddComponent<LayoutElement>()).flexibleWidth = 1;
            var up = CloneButton(tmpl, label+"Up", "►", row.transform);
            { var le=up.GetComponent<LayoutElement>() ?? up.AddComponent<LayoutElement>(); le.preferredWidth=32; le.flexibleWidth=0; }
            dn.GetComponent<Button>().onClick.AddListener(()=>{ PlayClick(); add(-step); val.text=fmt(); });
            up.GetComponent<Button>().onClick.AddListener(()=>{ PlayClick(); add( step); val.text=fmt(); });
            _liveLabels.Add(new System.Collections.Generic.KeyValuePair<TextMeshProUGUI,System.Func<string>>(val, fmt));
            return row;
        }

        // Value labels (steppers/sliders) re-formatted each panel tick so readouts like Mass don't lag
        // behind the live craft (mass changes as parts are added; the label must follow without a click).
        private readonly System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<TextMeshProUGUI,System.Func<string>>> _liveLabels
            = new System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<TextMeshProUGUI,System.Func<string>>>();
        private void RefreshLiveLabels()
        {
            for (int i=0;i<_liveLabels.Count;i++)
            { var t=_liveLabels[i].Key; if(t==null) continue; try{ string s=_liveLabels[i].Value(); if(t.text!=s) t.text=s; }catch{} }
        }

        // Draggable slider row in the panel style: label (left) · slider (flex) · value (right). The value
        // label live-refreshes each tick. Continuous test conditions (Speed/AoA/Flap) read much better as
        // sliders than ◄►-steppers. Re-sweep/redraw is handled by the periodic tick (MaybeSweep keys off
        // the value), so onValueChanged only needs to store the value + update the label.
        private GameObject MakeSlider(Transform parent, string label, float min, float max,
            System.Func<float> get, System.Action<float> set, System.Func<string> fmt)
        {
            var row = MakeRow(label+"Row", parent, 30);
            var lbl = MakeText(label, row.transform, _fontSize*0.8f);
            lbl.alignment = TextAlignmentOptions.Left;
            (lbl.gameObject.AddComponent<LayoutElement>()).preferredWidth = 88;
            try {

            var sGO = new GameObject(label+"Slider"); sGO.transform.SetParent(row.transform,false);
            var sRT = sGO.AddComponent<RectTransform>();
            var sLE = sGO.AddComponent<LayoutElement>(); sLE.flexibleWidth=1; sLE.preferredHeight=16; sLE.minWidth=80;
            var slider = sGO.AddComponent<UnityEngine.UI.Slider>();

            void Style(UnityEngine.UI.Image im, Color c){ im.color=c; if(_btnSprite!=null){ im.sprite=_btnSprite; im.type=UnityEngine.UI.Image.Type.Sliced; } }

            var bg = new GameObject("Background"); bg.transform.SetParent(sGO.transform,false);
            var bgImg = bg.AddComponent<UnityEngine.UI.Image>(); Style(bgImg, new Color(0.16f,0.20f,0.28f,1f));
            var bgRT = bg.GetComponent<RectTransform>(); bgRT.anchorMin=new Vector2(0,0.30f); bgRT.anchorMax=new Vector2(1,0.70f);
            bgRT.offsetMin=Vector2.zero; bgRT.offsetMax=Vector2.zero;

            var fillArea = new GameObject("Fill Area"); fillArea.transform.SetParent(sGO.transform,false);
            var faRT = fillArea.AddComponent<RectTransform>(); faRT.anchorMin=new Vector2(0,0.30f); faRT.anchorMax=new Vector2(1,0.70f);
            faRT.offsetMin=Vector2.zero; faRT.offsetMax=Vector2.zero;
            var fill = new GameObject("Fill"); fill.transform.SetParent(fillArea.transform,false);
            var fImg = fill.AddComponent<UnityEngine.UI.Image>(); Style(fImg, new Color(0.27f,0.55f,0.95f,1f));
            var fRT = fill.GetComponent<RectTransform>(); fRT.anchorMin=Vector2.zero; fRT.anchorMax=new Vector2(0,1); fRT.sizeDelta=new Vector2(8,0);

            var handleArea = new GameObject("Handle Slide Area"); handleArea.transform.SetParent(sGO.transform,false);
            var haRT = handleArea.AddComponent<RectTransform>(); haRT.anchorMin=Vector2.zero; haRT.anchorMax=Vector2.one;
            haRT.offsetMin=Vector2.zero; haRT.offsetMax=Vector2.zero;
            var handle = new GameObject("Handle"); handle.transform.SetParent(handleArea.transform,false);
            var hImg = handle.AddComponent<UnityEngine.UI.Image>(); Style(hImg, new Color(0.85f,0.91f,1f,1f));
            var hRT = handle.GetComponent<RectTransform>(); hRT.sizeDelta=new Vector2(12,14);   // stays inside its row (no overlap)

            slider.fillRect = fRT; slider.handleRect = hRT; slider.targetGraphic = hImg;
            slider.direction = UnityEngine.UI.Slider.Direction.LeftToRight;
            slider.minValue = min; slider.maxValue = max; slider.wholeNumbers = false;
            slider.value = Mathf.Clamp(get(), min, max);
            // Native-style hover highlight on the handle (lights up like SP2's own controls).
            slider.transition = UnityEngine.UI.Selectable.Transition.ColorTint;
            var sc = slider.colors; sc.normalColor=new Color(0.82f,0.82f,0.82f,1f); sc.highlightedColor=Color.white;
            sc.pressedColor=new Color(0.7f,0.7f,0.7f,1f); sc.selectedColor=Color.white; sc.fadeDuration=0.1f; slider.colors=sc;

            var val = MakeText(fmt(), row.transform, _fontSize*0.8f);
            val.alignment = TextAlignmentOptions.Right;
            (val.gameObject.AddComponent<LayoutElement>()).preferredWidth = 64;

            float lastSnd=-1f;
            slider.onValueChanged.AddListener(v=>{ set(v); val.text=fmt(); _sliderChangeAt=Time.unscaledTime;
                if (Time.unscaledTime - lastSnd > 0.06f) { lastSnd = Time.unscaledTime; PlayUiSound("SliderChanged"); } });
            AddHover(sGO);   // native hover sound on the slider
            _liveLabels.Add(new System.Collections.Generic.KeyValuePair<TextMeshProUGUI,System.Func<string>>(val, fmt));
            }
            catch (System.Exception e)
            {
                // Never let a UI hiccup take down the whole Aero panel — degrade to a live value label.
                Debug.Log($"[AeroUI] slider '{label}' build failed, using label: {e.Message}");
                var v2 = MakeText(fmt(), row.transform, _fontSize*0.8f); v2.alignment = TextAlignmentOptions.Right;
                (v2.gameObject.AddComponent<LayoutElement>()).flexibleWidth = 1;
                _liveLabels.Add(new System.Collections.Generic.KeyValuePair<TextMeshProUGUI,System.Func<string>>(v2, fmt));
            }
            return row;
        }

        // Only show a fixed-condition stepper when the current axes actually use it:
        //  Test Speed  → speed sweep (sets the range) or a Lift plot (force scales with speed)
        //  Test AoA    → speed sweep only (the fixed incidence)
        private void UpdateStepperVisibility()
        {
            if (_speedStepperRow) _speedStepperRow.SetActive(SweepIsSpeed || _yAxis==YAx.Lift);
            if (_aoaStepperRow)   _aoaStepperRow.SetActive(SweepIsSpeed);
        }

        private TextMeshProUGUI MakeTick(Transform parent, Vector2 anchoredTopLeft, float width, TextAlignmentOptions align)
        {
            var go=new GameObject("Tick"); go.transform.SetParent(parent,false);
            var t=go.AddComponent<TextMeshProUGUI>();
            t.fontSize=8.5f; t.color=new Color(0.7f,0.78f,0.9f); t.alignment=align; t.richText=false;
            if(_font!=null)t.font=_font;
            var rt=go.GetComponent<RectTransform>();
            rt.anchorMin=new Vector2(0,1); rt.anchorMax=new Vector2(0,1); rt.pivot=new Vector2(0,1);
            rt.anchoredPosition=anchoredTopLeft; rt.sizeDelta=new Vector2(width,14);
            return t;
        }

        private void TogglePanel()
        {
            Debug.Log($"[AeroUI] TogglePanel visible={_panelVisible} section={(_aeroSection!=null)}");
            PlayClick();
            _panelVisible = !_panelVisible;
            if (_aeroSection != null) _aeroSection.SetActive(_panelVisible);
            if (_panelVisible) _autoVerifyPending = true;
            if (!_panelVisible) { CleanupProbe(); _solReady=false; _solKey=""; }
        }

        // ═══════════════════════════════════════════════════════════════════════
        //  REAL-SOLVER PROBE (feasibility spike) — stand up SP2's own wing-physics
        //  solver on the selected wing (exactly like TestWingGenerator), drive it with
        //  a synthetic freestream, and log the RAW per-slice lift/drag + solved AoA.
        //  This is the genuine in-game lifting-line solver, not a reimplementation.
        // ═══════════════════════════════════════════════════════════════════════
        private GameObject _probeGO; private object _probeMgr, _probeRuntimeOut; private System.Type _wpmType;
        private Vector3 _probeWind; private float _probeArea;
        private object _probeInputMgr; private bool _probeHasSurfaces; private object _probeWingScript;
        private object _probeWingOverride;
        private float _flapDeflect;   // -1..1 Flaps-axis value applied to FLAP-type surfaces (Test Flap slider)
        private float _slatDeflect;   // -1..1 Flaps-axis value applied to SLAT-type surfaces (Test Slat slider)
        // Leading-edge / slat-class surfaces get the Slat slider; everything else gets the Flap slider — so
        // flaps and slats can be tested independently or together, even when the craft deploys them as one.
        private static bool IsSlatType(string typeName) => typeName=="Slat";

        // The control value for a given input axis during a flap analysis: only "Flaps" deflects;
        // Roll/Aileron/Pitch/Yaw/etc. stay neutral (they're differential / on other surfaces).
        private float AxisVal(string axis)
        {
            if (!string.IsNullOrEmpty(axis) && axis.Equals("Flaps", System.StringComparison.OrdinalIgnoreCase)) return _flapDeflect;
            return 0f;
        }

        // The editor craft's REAL AircraftControls (created in AircraftScript.Awake, so it exists in the
        // designer too). Driving its named axes (Flaps/Pitch/Roll…) lets SP2's own funky-trees parser
        // evaluate ANY control-surface input expression — custom combos, clamp(Flaps,-1,0), Flaps+Roll, etc.
        private object GetEditorControls()
        {
            try {
                object d=_designerInstanceProp?.GetValue(null); if(d==null) return null;
                object ac=_dAircraftProp?.GetValue(d); if(ac==null) return null;
                var cp=ac.GetType().GetProperty("Controls", BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance);
                return cp?.GetValue(ac);
            } catch { return null; }
        }
        // Push the panel's test conditions onto the controls so GetAxisGetter lambdas read them live.
        // Test Flap drives the Flaps axis; the other axes are held neutral (symmetric-flap analysis).
        private void SetTestControls(object controls)
        {
            if (controls==null) return;
            var t=controls.GetType(); var f=BindingFlags.Public|BindingFlags.Instance;
            void S(string n,float v){ try{ t.GetProperty(n,f)?.SetValue(controls,v); }catch{} }
            S("Flaps",_flapDeflect); S("Pitch",_testPitch); S("Roll",_testRoll); S("Yaw",_testYaw);
            S("Brake",0f); S("Trim",0f); S("Vtol",0f);
        }
        private float _testPitch, _testRoll, _testYaw;   // optional extra control axes (default neutral)
        private int _axdiag;   // limits funky-trees context diagnostic spam

        // ── Control-surface deflection preview (visual + per-surface readout) ─────────────
        // Drives each surface's real Angle (the property SP2 rotates the mesh with) from its actual
        // evaluated input expression, so flaps/slats/funky-trees surfaces visibly deploy in the editor —
        // no faked rotation, just SP2's own deploy math applied while editing.
        private bool _showDeflect, _deflectActive; private float _deflectRefresh;
        private System.Type _csScriptType;
        private class DeflDriver { public object cs; public object controls; public System.Func<float> eval; public float maxDeg; public string input; public bool invert; public PropertyInfo angle; }
        private readonly System.Collections.Generic.List<DeflDriver> _deflectDrivers = new System.Collections.Generic.List<DeflDriver>();

        // ── Surface highlight (native SP2 part outline) ───────────────────────────────────
        // Every control surface the calculator picks up is a real Part with a PartMaterialScript. Toggling
        // its IsHighlighted drives SP2's own HighlightPlus outline on exactly that part's renderers — so the
        // user can SEE which surfaces are being included in the aero, on any craft (no mesh/hinge math).
        private readonly System.Collections.Generic.List<object> _hiliteMats = new System.Collections.Generic.List<object>();
        private PropertyInfo _isHiliteProp;
        private void RegisterHilite(object partScript)
        {
            if (partScript==null) return;
            try {
                var pms = partScript.GetType().GetProperty("PartMaterialScript")?.GetValue(partScript);
                if (pms==null) return;
                if (_isHiliteProp==null) _isHiliteProp = pms.GetType().GetProperty("IsHighlighted");
                foreach (var m in _hiliteMats) if (ReferenceEquals(m, pms)) return;   // dedup
                _hiliteMats.Add(pms);
            } catch {}
        }
        private void ApplyHilite(bool on)
        {
            if (_isHiliteProp==null) return;
            foreach (var m in _hiliteMats) { try { _isHiliteProp.SetValue(m, on); } catch {} }
        }

        // ── Faithful mesh deflection ───────────────────────────────────────────────────────
        // The designer never builds the JWing articulation (_input is null), so flaps can't move on their
        // own. We recreate it without faking geometry: build the real probe wing at the current Test-Flap,
        // run SP2's own solver so its WingInputManager computes each surface's TargetTransform — which is
        // the exact deflection for that surface TYPE (plain flaps rotate, Fowler translate+rotate, slats
        // slide, spoilers pop, etc.) — then apply those transforms to the LIVE control-surface mesh objects
        // (each ControlSurfacePartScript._renderers[k].Transform). No hand-rolled hinge math, fully faithful.
        private bool _meshDeflectActive; private float _lastDeflectFlap=-999f, _lastDeflectSlat=-999f, _deflectRebuildTimer;
        private readonly System.Collections.Generic.List<(Transform t, Vector3 p, Quaternion r)> _deflectRest
            = new System.Collections.Generic.List<(Transform, Vector3, Quaternion)>();

        private FieldInfo _rtPos,_rtRot,_f3x,_f3y,_f3z,_q4,_q4x,_q4y,_q4z,_q4w;

        private void RtToUnity(object rt, bool mirrorY, out Vector3 pos, out Quaternion rot)
        {
            var t=rt.GetType();
            if (_rtPos==null){ _rtPos=t.GetField("pos"); _rtRot=t.GetField("rot"); }
            object p=_rtPos.GetValue(rt); object q=_rtRot.GetValue(rt);
            var pt=p.GetType(); if(_f3x==null){_f3x=pt.GetField("x");_f3y=pt.GetField("y");_f3z=pt.GetField("z");}
            float px=(float)_f3x.GetValue(p), py=(float)_f3y.GetValue(p), pz=(float)_f3z.GetValue(p);
            if(_q4==null)_q4=q.GetType().GetField("value");
            object v4=_q4.GetValue(q); var vt=v4.GetType();
            if(_q4x==null){_q4x=vt.GetField("x");_q4y=vt.GetField("y");_q4z=vt.GetField("z");_q4w=vt.GetField("w");}
            float qx=(float)_q4x.GetValue(v4), qy=(float)_q4y.GetValue(v4), qz=(float)_q4z.GetValue(v4), qw=(float)_q4w.GetValue(v4);
            // SP2's GetTransformInMirroredYSpace for flipped wings: pos.y→-pos.y, rot.(x,z)→-(x,z).
            if (mirrorY) { py=-py; qx=-qx; qz=-qz; }
            pos=new Vector3(px,py,pz);
            rot=new Quaternion(qx,qy,qz,qw);
        }

        private void RestoreMeshDeflection()
        {
            foreach (var e in _deflectRest) { try { if (e.t!=null) e.t.SetLocalPositionAndRotation(e.p, e.r); } catch {} }
            _deflectRest.Clear();
        }

        // Cached readout text + surface count, built by the SAME probe pass that drives the mesh, so the
        // numbers shown and the motion shown can never disagree.
        private string _surfReadout=""; private int _surfCount;
        private bool _probeFlippedOverride;

        // ONE source of truth for every wing on the craft. For each JWing: build SP2's real probe at the
        // current Test-Flap, let the solver compute the range-applied per-surface Inputs[] AND the per-type
        // TargetTransforms, then (a) apply those transforms to the live meshes (mirrored for flipped wings,
        // exactly as SP2's ApplyTransforms) and (b) report each surface's REAL engaged value (Inputs[], not
        // the raw expression). Readout == mesh == aero, on any craft, any wing layout, any surface type.
        private void RebuildMeshDeflection()
        {
            RestoreMeshDeflection();                       // back to rest so we re-capture clean bases
            var rd=new System.Text.StringBuilder(); int nSurf=0, wings=0;
            var surfDbg=new System.Text.StringBuilder($"flap={_flapDeflect:0.00} slat={_slatDeflect:0.00}\n");   // DIAG: full per-surface dump
            var f=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance;
            var icsType = FindType("Assets.Scripts.Craft.Parts.Modifiers.InputControllerScript");
            try
            {
                if (_wsType==null || _wsDataProp==null) return;
                var scriptsFld = _wsType.GetField("_controlSurfaceScripts", BindingFlags.NonPublic|BindingFlags.Instance);

                foreach (var wmb in Object.FindObjectsByType<MonoBehaviour>(FindObjectsSortMode.None))
                {
                    if (wmb==null || wmb.GetType()!=_wsType) continue;
                    object data=null; try { data=_wsDataProp.GetValue(wmb); } catch {}
                    if (data==null) continue;
                    bool flipped=false; try { flipped=(bool)(data.GetType().GetProperty("Flipped",f)?.GetValue(data) ?? false); } catch {}

                    // Build the probe for THIS wing, in its own flipped space, and solve.
                    _probeWingOverride=data; _useLiveInputs=false; _probeFlippedOverride=flipped;
                    bool ok=false; try { ok=BuildProbeWing(); } catch {}
                    if (!ok) { CleanupProbe(); continue; }
                    wings++;
                    try
                    {
                        var scripts = scriptsFld?.GetValue(wmb) as System.Array;
                        if (scripts==null) { CleanupProbe(); continue; }

                        var probeTs = _probeRuntimeOut?.GetType().GetField("ControlSurfaceTransforms")?.GetValue(_probeRuntimeOut) as System.Array;
                        int ptsLen = probeTs?.Length ?? 0;
                        // Wing-root frames: the probe wing is parented under _probeGO; the live wing IS this script.
                        // A surface's pose RELATIVE TO ITS WING ROOT is identical between probe and live (same build),
                        // so we read the probe's solved surface pose in probe-wing space and re-impose it in live-wing
                        // space, in WORLD coords — live part parenting, wing pose and handedness all cancel out.
                        Transform probeWingT = _probeGO!=null ? _probeGO.transform : null;
                        Transform liveWingT = (wmb as MonoBehaviour).transform;

                        RunSolverPoint(50f, 0f, 2, out _, out _, out _);   // solve → probe TargetTransforms + Inputs[]
                        try { _probeInputMgr?.GetType().GetMethod("ApplyTransforms")?.Invoke(_probeInputMgr, null); } catch {}  // deflect the probe surfaces
                        float[] inputs = NativeFloatArray(_probeInputMgr?.GetType().GetProperty("Inputs")?.GetValue(_probeInputMgr));
                        var rtData = _probeInputMgr?.GetType().GetProperty("ControlSurfaceRuntimeData")?.GetValue(_probeInputMgr) as System.Array;
                        // Per-surface deflection target (rotation about the hinge) — for the signed-angle readout.
                        object ttArr = _probeInputMgr?.GetType().GetProperty("TargetTransforms")?.GetValue(_probeInputMgr);
                        var ttGet = ttArr?.GetType().GetMethod("get_Item");
                        int ttLen = ttArr!=null ? System.Convert.ToInt32(ttArr.GetType().GetProperty("Length").GetValue(ttArr)) : 0;

                        int g=0, inIdx=0;
                        for (int j=0; j<scripts.Length; j++)
                        {
                            object cs = scripts.GetValue(j);
                            string expr="(none)";
                            try {
                                object ps = cs?.GetType().GetProperty("PartScript",f)?.GetValue(cs);
                                var getMods=ps?.GetType().GetMethod("GetModifiers")?.MakeGenericMethod(icsType);
                                var mods=getMods?.Invoke(ps,null) as System.Collections.IEnumerable;
                                if (mods!=null) foreach (var ctrl in mods)
                                {
                                    object icd=ctrl.GetType().GetProperty("InputController")?.GetValue(ctrl);
                                    string nm=icd?.GetType().GetProperty("Name")?.GetValue(icd) as string ?? "";
                                    if (!nm.StartsWith("controlsurface-")) continue;
                                    expr=icd?.GetType().GetProperty("Input")?.GetValue(icd) as string ?? ""; break;
                                }
                            } catch {}

                            int inCount=1; try { if(rtData!=null && j<rtData.Length){ var r=rtData.GetValue(j); inCount=(int)r.GetType().GetProperty("InputCount").GetValue(r);} } catch {}
                            float val = (inputs!=null && inIdx<inputs.Length) ? inputs[inIdx] : 0f;
                            inIdx += System.Math.Max(1,inCount);

                            // Signed hinge-deflection angle for THIS surface (first mesh), from the solver's
                            // TargetTransform. SP2 convention: −X-rotation = trailing edge down (deploy).
                            float ang=0f;
                            try {
                                if (ttArr!=null && ttGet!=null && g<ttLen) {
                                    object rt=ttGet.Invoke(ttArr, new object[]{g});
                                    RtToUnity(rt, false, out _, out Quaternion q);
                                    ang = 2f*Mathf.Atan2(q.x, q.w)*Mathf.Rad2Deg;
                                    if (ang>180f) ang-=360f; else if (ang<-180f) ang+=360f;
                                }
                            } catch {}
                            if (nSurf < 18) { string ex = string.IsNullOrEmpty(expr) ? "(none)" : (expr.Length>20 ? expr.Substring(0,19)+"…" : expr); rd.Append($"  <color=#cde>{ex}</color>  <b>{val:+0.00;-0.00;0.00}</b> <color=#ffd27f>{ang:+0;-0;0}°</color>\n"); }
                            else if (nSurf == 18) rd.Append("  …\n");
                            string sType=""; try { sType=cs?.GetType().GetProperty("ControlSurface")?.GetValue(cs)?.GetType().Name ?? ""; } catch {}
                            surfDbg.Append($"[{sType}] \"{expr}\" = {val:+0.00;-0.00;0.00}  {ang:+0;-0;0}deg\n");   // DIAG
                            nSurf++;

                            var rends = cs?.GetType().GetField("_renderers", BindingFlags.NonPublic|BindingFlags.Instance)?.GetValue(cs) as System.Array;
                            if (rends!=null) for (int k=0;k<rends.Length;k++)
                            {
                                object r=rends.GetValue(k);
                                var tr = r?.GetType().GetProperty("Transform")?.GetValue(r) as Transform;
                                if (tr==null) { g++; continue; }
                                if (g>=ptsLen || probeWingT==null) { g++; continue; }
                                var pt = probeTs.GetValue(g) as Transform;   // probe surface, solved/deflected
                                if (pt==null) { g++; continue; }
                                // Re-impose the probe's solved surface pose (expressed in probe-wing space) onto the
                                // live surface in live-wing space, in WORLD coords — part parenting, wing pose and
                                // handedness all cancel out, so it matches flight for every surface type.
                                Vector3 wPos = probeWingT.InverseTransformPoint(pt.position);
                                Quaternion wRot = Quaternion.Inverse(probeWingT.rotation) * pt.rotation;
                                _deflectRest.Add((tr, tr.localPosition, tr.localRotation));
                                tr.SetPositionAndRotation(liveWingT.TransformPoint(wPos), liveWingT.rotation * wRot);
                                g++;
                            }
                        }
                    }
                    catch { }
                    finally { CleanupProbe(); }
                }
                _ = wings;
            }
            catch { }
            finally
            {
                _probeWingOverride=null; _probeFlippedOverride=false;
                _surfReadout=rd.ToString(); _surfCount=nSurf;
                try { System.IO.File.WriteAllText(@"E:\Temp\aero_surf.txt", surfDbg.ToString()); } catch {}   // DIAG
            }
        }

        private void NeutralizeControls(object controls)
        {
            if (controls==null) return;
            var t=controls.GetType(); var f=BindingFlags.Public|BindingFlags.Instance;
            void S(string n){ try{ t.GetProperty(n,f)?.SetValue(controls,0f); }catch{} }
            S("Flaps"); S("Pitch"); S("Roll"); S("Yaw"); S("Brake"); S("Trim"); S("Vtol");
        }

        private System.Func<float> MakeEval(object controls, string input, object partScript)
        {
            var gag=controls?.GetType().GetMethod("GetAxisGetter");
            if (gag!=null) try { return (System.Func<float>)gag.Invoke(controls, new object[]{ input, -1f, partScript, false }); } catch {}
            string ax=input; return ()=>AxisVal(ax);
        }

        private void RefreshDeflectionDrivers()
        {
            ApplyHilite(false);          // drop highlight on the previous set before rebuilding it
            _deflectDrivers.Clear(); _hiliteMats.Clear();
            var f=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance;

            // Path A — legacy ControlSurfaceScript wings: we can also visually deflect these (set Angle).
            if (_csScriptType==null) _csScriptType = FindType("Assets.Scripts.Craft.Parts.Modifiers.ControlSurfaceScript");
            if (_csScriptType!=null)
            {
                var acP=_csScriptType.GetProperty("AircraftControls",f); var csdP=_csScriptType.GetProperty("ControlSurface",f);
                var wsP=_csScriptType.GetProperty("WingScript",f); var angP=_csScriptType.GetProperty("Angle",f);
                if (acP!=null && csdP!=null && angP!=null)
                foreach (var cs in Object.FindObjectsByType(_csScriptType, FindObjectsSortMode.None))
                {
                    try {
                        object controls=acP.GetValue(cs); object csData=csdP.GetValue(cs); if(controls==null||csData==null) continue;
                        var cdt=csData.GetType();
                        string input=cdt.GetProperty("InputId")?.GetValue(csData) as string ?? "";
                        float maxDeg=System.Convert.ToSingle(cdt.GetProperty("MaxDeflectionDegree")?.GetValue(csData) ?? 0f);
                        bool invert=false; try{ invert=(bool)(cdt.GetProperty("Invert")?.GetValue(csData) ?? false); }catch{}
                        object ws=wsP?.GetValue(cs); object psc=ws?.GetType().GetProperty("PartScript")?.GetValue(ws);
                        RegisterHilite(psc);
                        _deflectDrivers.Add(new DeflDriver{ cs=cs, controls=controls, eval=MakeEval(controls,input,psc), maxDeg=maxDeg, input=input, invert=invert, angle=angP });
                    } catch {}
                }
            }

            // Path B — JWing (procedural) control surfaces: readout only (deflection is computed by a Burst
            // physics job that doesn't run in the editor, so we can't faithfully move the mesh — but we CAN
            // evaluate each surface's real input expression and report its exact deflection).
            var icsType = FindType("Assets.Scripts.Craft.Parts.Modifiers.InputControllerScript");
            if (_wsType!=null && icsType!=null)
            {
                var csListField = _wsType.GetField("_controlSurfaces", f);
                foreach (var mb in Object.FindObjectsByType<MonoBehaviour>(FindObjectsSortMode.None))
                {
                    if (mb==null || mb.GetType()!=_wsType) continue;
                    var csList = csListField?.GetValue(mb) as System.Collections.IList; if(csList==null) continue;
                    foreach (var csData in csList)
                    {
                        try {
                            object part=csData.GetType().GetProperty("Part",f)?.GetValue(csData);
                            object ps=part?.GetType().GetProperty("PartScript",f)?.GetValue(part);
                            RegisterHilite(ps);     // highlight this control-surface part on the craft
                            object ac=ps?.GetType().GetProperty("Aircraft",f)?.GetValue(ps);
                            object controls=ac?.GetType().GetProperty("Controls",f)?.GetValue(ac);
                            var getMods=ps?.GetType().GetMethod("GetModifiers")?.MakeGenericMethod(icsType);
                            var mods=getMods?.Invoke(ps,null) as System.Collections.IEnumerable;
                            if (mods==null || controls==null) continue;
                            foreach (var ctrl in mods)
                            {
                                object icd=ctrl.GetType().GetProperty("InputController")?.GetValue(ctrl);
                                string name=icd?.GetType().GetProperty("Name")?.GetValue(icd) as string ?? "";
                                string input=icd?.GetType().GetProperty("Input")?.GetValue(icd) as string ?? "";
                                if (!name.StartsWith("controlsurface-")) continue;
                                _deflectDrivers.Add(new DeflDriver{ cs=null, controls=controls, eval=MakeEval(controls,input,ps), maxDeg=0f, input=input, invert=false, angle=null });
                            }
                        } catch {}
                    }
                }
            }
        }

        private float DeflAngle(DeflDriver d)
        {
            float num=0f; try{ num=d.eval(); }catch{}
            if (d.invert) num=-num;
            return num * d.maxDeg;
        }

        private float DeflFraction(DeflDriver d)
        {
            float num=0f; try{ num=d.eval(); }catch{}
            if (d.invert) num=-num;
            return num;
        }

        private void DriveDeflections()
        {
            for (int i=0;i<_deflectDrivers.Count;i++)
            { var d=_deflectDrivers[i]; try { SetTestControls(d.controls); if (d.angle!=null) d.angle.SetValue(d.cs, DeflAngle(d)); } catch {} }
        }

        private void ResetDeflections()
        {
            for (int i=0;i<_deflectDrivers.Count;i++)
            { var d=_deflectDrivers[i]; try { NeutralizeControls(d.controls); if (d.angle!=null) d.angle.SetValue(d.cs, 0f); } catch {} }
            _deflectDrivers.Clear();
        }

        private void BuildDeflectReadout()
        {
            if (_verifyText==null) return;
            if (!_showDeflect) { _verifyText.text=""; return; }
            // Values come from the SAME probe pass that moves the meshes (_surfReadout), so the numbers
            // shown are exactly the range-applied inputs that drive the deflection and the aero.
            var sb=new System.Text.StringBuilder();
            sb.Append($"<color=#aaffdd><b>Surfaces</b> (n={_surfCount}) @ Flap {_flapDeflect*100f:F0}% · Slat {_slatDeflect*100f:F0}%</color>\n");
            if (string.IsNullOrEmpty(_surfReadout)) sb.Append("  (computing…)");
            else sb.Append(_surfReadout);
            _verifyText.text=sb.ToString();
        }

        // Control surfaces (ControlSurface[]) of the selected wing — from its live JWingScript.
        private System.Array GetWingSurfaces(object wing, System.Type csType)
        {
            _probeWingScript = null;
            try {
                if (_wsType!=null && _wsDataProp!=null)
                foreach (var mb in Object.FindObjectsByType<MonoBehaviour>(FindObjectsSortMode.None))
                {
                    if (mb?.GetType()!=_wsType) continue;
                    object data=null; try{ data=_wsDataProp.GetValue(mb); }catch{}
                    if (!ReferenceEquals(data, wing)) continue;
                    _probeWingScript = mb;
                    var fi = _wsType.GetField("_controlSurfacesArray", BindingFlags.NonPublic|BindingFlags.Instance);
                    var arr = fi?.GetValue(mb) as System.Array;
                    if (arr!=null && arr.Length>0) return arr;
                    break;
                }
            } catch {}
            return System.Array.CreateInstance(csType, 0);
        }

        // Wire each control-surface input to its real control axis (per SP2's SetupInputs): drive only
        // the "Flaps" axis from Test Flap, others neutral. Handles flaperons, ailerons, slats, custom
        // inputs, and per-surface deflection limits. Falls back to driving everything if mapping fails.
        private void WireControlInputs(object inputMgr, System.Type wimType, int nCs)
        {
            var setGetter = wimType.GetMethod("SetInputGetter");
            bool perAxis = false;
            // Faithful path: drive the editor craft's real AircraftControls and use SP2's own funky-trees
            // parser (GetAxisGetter) to evaluate each surface's input expression — so custom combos work.
            // NB: each part's Aircraft.Controls is its OWN instance (≠ designer.Aircraft.Controls), so the
            // test values must be set on the part's controls below — this is only the fallback.
            object controls = GetEditorControls();
            try {
                var f=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance;
                var csList = _wsType.GetField("_controlSurfaces", f)?.GetValue(_probeWingScript) as System.Collections.IList;
                var icsType = FindType("Assets.Scripts.Craft.Parts.Modifiers.InputControllerScript");
                if (csList!=null && icsType!=null)
                {
                    var sb=new System.Text.StringBuilder("[AeroSolver] control inputs:  ");
                    for (int i=0; i<csList.Count && i<nCs; i++)
                    {
                        object csData=csList[i];
                        object part=csData.GetType().GetProperty("Part",f)?.GetValue(csData);
                        object ps=part?.GetType().GetProperty("PartScript",f)?.GetValue(part);
                        var getMods=ps?.GetType().GetMethod("GetModifiers")?.MakeGenericMethod(icsType);
                        var mods=getMods?.Invoke(ps,null) as System.Collections.IEnumerable;
                        if (mods==null) continue;
                        // Use the PART'S OWN aircraft controls — the funky-trees context (SetupContext)
                        // resolves Flaps/Roll/… from contextPart.Aircraft.Controls, so we must set the test
                        // values on THAT same controls object (designer.Aircraft.Controls may differ).
                        object partAircraft=null; try{ partAircraft = ps.GetType().GetProperty("Aircraft",f)?.GetValue(ps); }catch{}
                        object partControls = (partAircraft!=null)
                            ? partAircraft.GetType().GetProperty("Controls", BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance)?.GetValue(partAircraft)
                            : controls;
                        SetTestControls(partControls);
                        // Feed FLAP-type surfaces the Test-Flap value and SLAT-type surfaces the Test-Slat value
                        // (on the SAME Flaps axis their funky-trees read) — so flaps and slats are independently
                        // testable. The expression is then evaluated WITH this value and baked (constant getter),
                        // because all surfaces share one controls object and a live getter would see only the last.
                        string csTypeName=""; try { object csObj=csData.GetType().GetProperty("ControlSurface",f)?.GetValue(csData); csTypeName=csObj?.GetType().Name ?? ""; } catch {}
                        bool isSlat = IsSlatType(csTypeName);
                        float surfFlaps = isSlat ? _slatDeflect : _flapDeflect;
                        try { partControls.GetType().GetProperty("Flaps",f)?.SetValue(partControls, surfFlaps); } catch {}
                        var pgag = partControls?.GetType().GetMethod("GetAxisGetter");
                        foreach (var ctrl in mods)
                        {
                            object icd=ctrl.GetType().GetProperty("InputController")?.GetValue(ctrl);
                            string name=icd?.GetType().GetProperty("Name")?.GetValue(icd) as string ?? "";
                            string axis=icd?.GetType().GetProperty("Input")?.GetValue(icd) as string ?? "";
                            if (!name.StartsWith("controlsurface-")) continue;
                            if (!int.TryParse(name.Substring(15), out int idx)) continue;
                            // Evaluate via SP2's parser (handles funky-trees + custom combos), then apply the
                            // SAME MinValue/MaxValue/Invert mapping InputControllerScript.Value does in flight —
                            // WITHOUT this, a raw -1 stays -1 instead of mapping to +deploy (which is exactly why
                            // the editor's slats sat at 0 and flaps came out the wrong sign). This makes the probe
                            // inputs identical to flight's controller.Value.
                            System.Func<float> raw = null;
                            if (pgag!=null && ps!=null)
                                try { raw = (System.Func<float>) pgag.Invoke(partControls, new object[]{ axis, -1f, ps, false }); }
                                catch (System.Exception ex){ if(_axdiag<6){ Debug.Log($"[AeroSolver] GetAxisGetter EXC \"{axis}\": {(ex.InnerException?.Message ?? ex.Message)}"); _axdiag++; } }
                            if (raw==null) { string ax=axis; raw = ()=>AxisVal(ax); }   // fallback: literal Flaps only
                            float minV=1f, maxV=1f; bool inv=false; string invType="";
                            try { minV=System.Convert.ToSingle(icd.GetType().GetProperty("MinValue")?.GetValue(icd) ?? 1f); } catch {}
                            try { maxV=System.Convert.ToSingle(icd.GetType().GetProperty("MaxValue")?.GetValue(icd) ?? 1f); } catch {}
                            try { inv=(bool)(icd.GetType().GetProperty("Invert")?.GetValue(icd) ?? false); } catch {}
                            try { invType=icd.GetType().GetProperty("InvertType")?.GetValue(icd)?.ToString() ?? ""; } catch {}
                            // Bake the value NOW (Flaps already set per-type above) into a constant getter.
                            // Slats commonly have NO manual axis ("Disabled" → AoA auto-deploy only), so for slat-
                            // class surfaces we FORCE the input straight to the Test-Slat amount (its [0,1] range
                            // maps that to deployment). Flaps/ailerons keep their real expression so differential
                            // surfaces stay neutral at Roll/Pitch 0.
                            float bv;
                            if (isSlat) { bv = Mathf.Abs(_slatDeflect); }
                            else {
                                float bnum=0f; try { bnum=raw(); } catch {}
                                if (inv && invType=="Axis") bnum=-bnum;
                                bv = (bnum<0f) ? (-bnum*minV) : (bnum*maxV);
                                if (inv && invType=="Output") bv=-bv;
                                // Flap-named surface that DOESN'T move with the Flaps axis ⇒ it's wired to a custom
                                // variable (e.g. "FlapOut") that we can't reach. Detect by sweeping Flaps ±1: if the
                                // expression doesn't change, force it from Test Flap so it still deploys. Real
                                // Flaps-driven flaps (incl. clamp/funky-trees) DO change, so they keep evaluating.
                                if (axis!=null && axis.IndexOf("flap", System.StringComparison.OrdinalIgnoreCase)>=0)
                                {
                                    try {
                                        var fp=partControls.GetType().GetProperty("Flaps",f);
                                        fp?.SetValue(partControls, 1f);  float vp=raw();
                                        fp?.SetValue(partControls,-1f);  float vm=raw();
                                        fp?.SetValue(partControls, surfFlaps);                       // restore
                                        if (Mathf.Abs(vp-vm) < 0.0005f) bv = _flapDeflect;           // unresponsive → force-deploy
                                    } catch {}
                                }
                            }
                            System.Func<float> ev = () => bv;
                            setGetter.Invoke(inputMgr, new object[]{ i, idx, ev });
                            float evv=0f; try{ evv=ev(); }catch{}
                            sb.Append($"cs{i}:\"{axis}\"={evv:F2} "); perAxis=true;
                        }
                    }
                    Debug.Log(sb.ToString());
                }
            } catch (System.Exception e){ Debug.Log("[AeroSolver] input-wire EXC "+e.Message); }

            if (!perAxis)   // fallback: drive all inputs from Test Flap (old behaviour)
            {
                try {
                    var csrd=wimType.GetProperty("ControlSurfaceRuntimeData")?.GetValue(inputMgr) as System.Array;
                    System.Func<float> flapFn=()=>_flapDeflect; int n=csrd?.Length ?? nCs;
                    for (int c=0;c<n;c++){ int ic=1; try{var rd=csrd.GetValue(c);ic=(int)rd.GetType().GetProperty("InputCount").GetValue(rd);}catch{}
                        for(int idx=0;idx<Mathf.Max(1,ic);idx++) setGetter.Invoke(inputMgr,new object[]{c,idx,flapFn}); }
                    Debug.Log("[AeroSolver] control inputs: (fallback — all driven by Test Flap)");
                } catch {}
            }
        }

        // For live-vs-probe comparison: replay the LIVE craft's evaluated per-input values into the
        // probe (flat Inputs array, surface order matches), so every surface deflects exactly as flight.
        private float[] _liveInputs; private bool _useLiveInputs;
        private void WireControlInputsFromArray(object inputMgr, System.Type wimType, float[] vals)
        {
            try {
                var csrd = wimType.GetProperty("ControlSurfaceRuntimeData")?.GetValue(inputMgr) as System.Array;
                var setGetter = wimType.GetMethod("SetInputGetter");
                int off=0, n=csrd?.Length ?? 0;
                for (int c=0;c<n;c++)
                {
                    int ic=1; try{ var rd=csrd.GetValue(c); ic=(int)rd.GetType().GetProperty("InputCount").GetValue(rd); }catch{}
                    ic=Mathf.Max(1,ic);
                    for (int idx=0; idx<ic; idx++){ int fi=off+idx; float v=(vals!=null && fi<vals.Length)?vals[fi]:0f; setGetter.Invoke(inputMgr, new object[]{ c, idx, (System.Func<float>)(()=>v) }); }
                    off+=ic;
                }
            } catch {}
        }
        private static float[] NativeFloatArray(object na)
        {
            if (na==null) return null;
            try { int len=(int)na.GetType().GetProperty("Length").GetValue(na); var item=na.GetType().GetMethod("get_Item");
                  var a=new float[len]; for(int i=0;i<len;i++) a[i]=System.Convert.ToSingle(item.Invoke(na,new object[]{i})); return a; } catch { return null; }
        }

        // ── Real-solver cached AoA sweep (the graph draws from this) ──────────
        private bool _solReady; private string _solKey="";
        private float _sliderChangeAt; private string _sweepPendKey=""; private float _sweepPendAt;
        private float _solSpeedMs, _solArea;
        private readonly System.Collections.Generic.List<float> _solAoA = new System.Collections.Generic.List<float>();
        private readonly System.Collections.Generic.List<float> _solCL  = new System.Collections.Generic.List<float>();
        private readonly System.Collections.Generic.List<float> _solCD  = new System.Collections.Generic.List<float>();
        private readonly System.Collections.Generic.List<float> _solCM  = new System.Collections.Generic.List<float>();
        private struct VerifyResult
        {
            public float flap, area, span, ar, clMax, clMin, clAt0, cdAt0, slope, zeroLift, stallPos, stallNeg, ldMax, ldAoA;
            public int points;
            public bool ready;
        }

        private void CleanupProbe()
        {
            // OnDestroy → Cleanup() frees the shared MallocPtrs + PhysicsSlices AND the manager's
            // own arrays. Do NOT also call runtimeOut.Dispose() — that double-frees and corrupts the heap.
            try { if (_probeMgr!=null && _wpmType!=null) _wpmType.GetMethod("OnDestroy")?.Invoke(_probeMgr,null); } catch {}
            _probeMgr=null; _probeRuntimeOut=null;
            if (_probeGO!=null) { Destroy(_probeGO); _probeGO=null; }   // Destroy cleans up the mesh GameObjects
        }

        private static System.Type FindType(string full)
        {
            foreach (var asm in System.AppDomain.CurrentDomain.GetAssemblies())
            { var t=asm.GetType(full); if(t!=null) return t; }
            return null;
        }
        private static void SetF(object obj, string name, object val)
        { var fi=obj.GetType().GetField(name); if(fi!=null) fi.SetValue(obj,val); }
        private static float F3mag(object f3)
        {
            if(f3==null) return 0f; var t=f3.GetType();
            float x=System.Convert.ToSingle(t.GetField("x").GetValue(f3));
            float y=System.Convert.ToSingle(t.GetField("y").GetValue(f3));
            float z=System.Convert.ToSingle(t.GetField("z").GetValue(f3));
            return Mathf.Sqrt(x*x+y*y+z*z);
        }
        private static float F3dot(object f3, Vector3 dir)
        {
            if(f3==null) return 0f; var t=f3.GetType();
            float x=System.Convert.ToSingle(t.GetField("x").GetValue(f3));
            float y=System.Convert.ToSingle(t.GetField("y").GetValue(f3));
            float z=System.Convert.ToSingle(t.GetField("z").GetValue(f3));
            return x*dir.x + y*dir.y + z*dir.z;
        }

        // Diagnostics: dump every field name=value of a struct (float3 prints as (x,y,z)).
        private static void DumpFields(object o, string label)
        {
            if (o==null){ Debug.Log($"[AeroSolver] {label}=null"); return; }
            var sb=new System.Text.StringBuilder($"[AeroSolver] {label}: ");
            foreach (var fi in o.GetType().GetFields(BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance))
            { object v=null; try{v=fi.GetValue(o);}catch{} sb.Append(fi.Name+"="+v+"  "); }
            Debug.Log(sb.ToString());
        }
        // Sum chordLength*spanWidth over the physics slices → the area the solver actually uses.
        private void DumpSlices()
        {
            try {
                var f=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance;
                object na=null;
                var pi=_wpmType.GetProperty("PhysicsSlices",f) ?? _wpmType.GetProperty("Slices",f);
                if (pi!=null) na=pi.GetValue(_probeMgr);
                else { var fi=_wpmType.GetField("_slices",f); if(fi!=null) na=fi.GetValue(_probeMgr); }
                if (na==null){ Debug.Log("[AeroSolver] slices: <none>"); return; }
                int len=(int)na.GetType().GetProperty("Length").GetValue(na);
                var item=na.GetType().GetMethod("get_Item");
                float areaSum=0f; object mid=null;
                for(int i=0;i<len;i++){ object s=item.Invoke(na,new object[]{i});
                    if(i==len/2) mid=s;
                    float c=GetF(s,"chordLength"), w=GetF(s,"spanWidth"); areaSum+=c*w; }
                Debug.Log($"[AeroSolver] physics area Σ(chord·span)={areaSum:F2} m² (UI says {_rep.area:F2})");
                DumpFields(mid, "sliceMid");
            } catch (System.Exception e){ Debug.Log("[AeroSolver] DumpSlices EXC "+e.Message); }
        }
        private static float GetF(object o,string name){ try{ var fi=o.GetType().GetField(name); return fi!=null?System.Convert.ToSingle(fi.GetValue(o)):0f; }catch{return 0f;} }

        // Build SP2's real wing physics manager for the selected wing (crash-free, destroyed by CleanupProbe).
        private bool BuildProbeWing()
        {
            CleanupProbe();
            try
            {
                object wing = _probeWingOverride ?? GetToolWing(out _);
                if (wing == null) return false;
                _probeArea = PF(wing, _jdArea2);   // the wing's real area for CL/CD normalisation (_rep.area is 0 at probe time)
                var slicesList = _jdSlices?.GetValue(wing) as System.Collections.IList;
                if (slicesList == null || slicesList.Count < 2) return false;

                var iwsType = FindType("Assets.Scripts.Craft.Wings.InputWingSlice");
                var csType  = FindType("Assets.Scripts.Craft.Wings.ControlSurfaces.ControlSurface");
                var wbiType = FindType("Assets.Scripts.Craft.Wings.WingBuilderInput");
                var wbType  = FindType("Assets.Scripts.Craft.Wings.WingBuilder");
                var wimType = FindType("Assets.Scripts.Craft.Wings.Runtime.WingInputManager");
                _wpmType    = FindType("Assets.Scripts.Craft.Wings.Physics.WingPhysicsManager");
                if (iwsType==null||wbiType==null||wbType==null||wimType==null||_wpmType==null)
                { Debug.Log($"[AeroSolver] type miss iws={iwsType!=null} wbi={wbiType!=null} wb={wbType!=null} wim={wimType!=null} wpm={_wpmType!=null}"); return false; }

                var slicesArr = System.Array.CreateInstance(iwsType, slicesList.Count);
                for (int i=0;i<slicesList.Count;i++) slicesArr.SetValue(slicesList[i], i);

                // Control surfaces (flaps/slats/spoilers) of the SELECTED wing, so the real solver
                // computes their effect. Empty if none / not found → bare wing (unchanged behaviour).
                System.Array surfArr = GetWingSurfaces(wing, csType);
                _probeHasSurfaces = surfArr.Length > 0;

                _probeGO = new GameObject("AeroSolverProbe");
                var rb = _probeGO.AddComponent<Rigidbody>();
                rb.constraints = RigidbodyConstraints.FreezeAll; rb.useGravity = false;

                object wbi = System.Activator.CreateInstance(wbiType);
                SetF(wbi,"inputSlices",slicesArr); SetF(wbi,"surfaces",surfArr);
                SetF(wbi,"flipped",_probeFlippedOverride); SetF(wbi,"parent",_probeGO.transform);
                SetF(wbi,"HideMainMesh",true); SetF(wbi,"GenerateControlSurfaceColliders",false);

                // Use the wing's OWN physics-sample count so slice resolution matches flight exactly
                int? samples = null;
                try { var ps=wing.GetType().GetProperty("PhysicsSamples")?.GetValue(wing); if(ps!=null) samples=(int?)ps; } catch {}

                MethodInfo genM=null;
                foreach (var mi in wbType.GetMethods(BindingFlags.Public|BindingFlags.Static))
                    if (mi.Name=="Generate" && mi.GetParameters().Length==2) { genM=mi; break; }
                _probeRuntimeOut = genM.Invoke(null, new object[]{ wbi, samples });
                object runtimeOut = _probeRuntimeOut;
                Debug.Log($"[AeroSolver] WingBuilder.Generate OK (samples={(samples.HasValue?samples.Value.ToString():"default")})");

                object inputMgr = System.Activator.CreateInstance(wimType, new object[]{ runtimeOut });
                _probeInputMgr = inputMgr;

                // Diagnostic: what kind of control surfaces does this wing have (flap/slat/aileron)?
                try {
                    var styles=new System.Text.StringBuilder("[AeroSolver] surface types: ");
                    foreach (var s in surfArr){ if(s==null){styles.Append("null ");continue;}
                        string st=s.GetType().Name;
                        object loc=null; try{loc=s.GetType().GetProperty("Location")?.GetValue(s);}catch{}
                        styles.Append($"{st}({loc}) "); }
                    Debug.Log(styles.ToString());
                } catch {}

                // Wire control-surface inputs. During a live comparison, copy the LIVE craft's already-
                // evaluated input values (so custom funky-trees expressions match exactly); otherwise map
                // the Flaps axis from Test Flap.
                if (_probeHasSurfaces)
                {
                    if (_useLiveInputs && _liveInputs!=null) WireControlInputsFromArray(inputMgr, wimType, _liveInputs);
                    else WireControlInputs(inputMgr, wimType, surfArr.Length);
                }

                _probeMgr = System.Activator.CreateInstance(_wpmType, new object[]{ runtimeOut, this, inputMgr, rb });
                _wpmType.GetProperty("DebugEnable")?.SetValue(_probeMgr, true);

                var wvg = _wpmType.GetProperty("WindVectorGetter");
                System.Func<Vector3> getter = () => _probeWind;
                wvg?.SetValue(_probeMgr, getter);

                // Bulletproofing: log the exact multipliers the real manager will apply (same the sim uses).
                try {
                    float fs   = System.Convert.ToSingle(_wpmType.GetProperty("ForceScale")?.GetValue(_probeMgr) ?? 1f);
                    float wd   = System.Convert.ToSingle(_wpmType.GetProperty("WaveDragMultiplier")?.GetValue(_probeMgr) ?? 1f);
                    float vm   = System.Convert.ToSingle(_wpmType.GetProperty("ViscousDragDueToLiftMultiplier", BindingFlags.Public|BindingFlags.Static)?.GetValue(null) ?? 0f);
                    Debug.Log($"[AeroSolver] multipliers  ForceScale={fs}  WaveDrag={wd}  ViscousDueToLift(static)={vm}");
                } catch {}
                return true;
            }
            catch (System.Exception e) { Debug.Log("[AeroSolver] build EXC " + e); return false; }
        }

        // Run the real solver to convergence at one airspeed/AoA and read raw CL/CD/CM (normalised by wing area).
        private bool RunSolverPoint(float V, float aoaDeg, int schedules, out float cl, out float cd, out float cm)
        {
            cl=0f; cd=0f; cm=0f;
            try
            {
                float a = aoaDeg*Mathf.Deg2Rad;
                _probeWind = new Vector3(0f, Mathf.Sin(a)*V, -Mathf.Cos(a)*V);   // gives the solver +a (verified)
                // Pump control-surface inputs so the Test-Flap deflection is read by the solver.
                if (_probeHasSurfaces && _probeInputMgr!=null)
                    try { _probeInputMgr.GetType().GetMethod("GetInputs")?.Invoke(_probeInputMgr, null); } catch {}
                var sched = _wpmType.GetMethod("ScheduleJobs");
                var onDone = _wpmType.GetMethod("OnJobsCompleted");
                for (int iter=0; iter<schedules; iter++)
                {
                    object tuple = sched.Invoke(_probeMgr, null);
                    var jh = tuple.GetType().GetField("Item1").GetValue(tuple);
                    jh.GetType().GetMethod("Complete").Invoke(jh, null);
                    onDone.Invoke(_probeMgr, null);
                }
                var dbg = _wpmType.GetMethod("GetDebugData").Invoke(_probeMgr, null);
                if (dbg == null) return false;
                var hv = dbg.GetType().GetProperty("HasValue");
                if (hv!=null && !(bool)hv.GetValue(dbg)) return false;
                object na = dbg.GetType().GetProperty("Value")!=null ? dbg.GetType().GetProperty("Value").GetValue(dbg) : dbg;
                int len = (int)na.GetType().GetProperty("Length").GetValue(na);
                var item = na.GetType().GetMethod("get_Item");
                // SIGNED lift: project each slice's lift vector onto the lift direction (perpendicular to
                // freestream, "up"). Magnitude alone loses the sign → negative-AoA lift read positive.
                Vector3 liftDir = new Vector3(0f, Mathf.Cos(a), Mathf.Sin(a));
                float liftN=0f, dragN=0f;
                for (int i=0;i<len;i++){ object d=item.Invoke(na,new object[]{i});
                    liftN += F3dot(d.GetType().GetField("liftForce").GetValue(d), liftDir);   // signed
                    dragN += F3mag(d.GetType().GetField("dragForce").GetValue(d)); }
                float q = 0.5f*1.225f*V*V; float area = _probeArea>0.05f?_probeArea:1f;
                cl = liftN/(q*area); cd = dragN/(q*area);

                // CM about quarter-chord from the real solver's net wing torque (pitch = span axis).
                try {
                    var of=_wpmType.GetField("_output", BindingFlags.NonPublic|BindingFlags.Instance);
                    object outArr=of?.GetValue(_probeMgr);
                    if(outArr!=null){ int ol=(int)outArr.GetType().GetProperty("Length").GetValue(outArr);
                        if(ol>0){ object fj=outArr.GetType().GetMethod("get_Item").Invoke(outArr,new object[]{0});
                            Vector3 torque=F3vec(fj.GetType().GetField("torque").GetValue(fj));
                            float span=_rep.span>0.1f?_rep.span:Mathf.Sqrt(area);
                            float c=area/span;                       // mean chord
                            cm = (q*area*c)>1f ? -torque.x/(q*area*c) : 0f;   // pitch about span (x); sign per convention
                        } } } catch {}
                return true;
            }
            catch { return false; }
        }

        // Sweep AoA through the REAL solver and cache the raw CL/CD curve (re-run only on change).
        private void RunSolverSweep(float V)
        {
            _solReady=false;
            if (!BuildProbeWing()) { CleanupProbe(); return; }
            var llsType = FindType("Assets.Scripts.Craft.Wings.Physics.LiftingLineSolver");
            var iterProp = llsType?.GetProperty("IterationsSetting", BindingFlags.Public|BindingFlags.Static);
            int saved = iterProp!=null ? (int)iterProp.GetValue(null) : 10;
            _solAoA.Clear(); _solCL.Clear(); _solCD.Clear(); _solCM.Clear();
            var pts = new System.Collections.Generic.List<Vector4>();   // (aoa, cl, cd, cm)
            try
            {
                iterProp?.SetValue(null, 80);
                // Sweep OUTWARD from 0° in both directions so each warm-started point starts from
                // attached flow (sweeping up from deep stall drags stall hysteresis into the linear range).
                RunSolverPoint(V, 0f, 40, out _, out _, out _);                 // converge attached flow first
                for (float aoa=0f; aoa<=45.01f; aoa+=2f)
                    if (RunSolverPoint(V, aoa, 8, out float cl, out float cd, out float cm)) pts.Add(new Vector4(aoa,cl,cd,cm));
                RunSolverPoint(V, 0f, 40, out _, out _, out _);                 // re-settle, then go negative
                for (float aoa=-2f; aoa>=-45.01f; aoa-=2f)
                    if (RunSolverPoint(V, aoa, 8, out float cl, out float cd, out float cm)) pts.Add(new Vector4(aoa,cl,cd,cm));
                pts.Sort((p,q)=>p.x.CompareTo(q.x));
                foreach (var p in pts){ _solAoA.Add(p.x); _solCL.Add(p.y); _solCD.Add(p.z); _solCM.Add(p.w); }
            }
            catch (System.Exception e){ Debug.Log("[AeroSolver] sweep EXC "+e.Message); }
            finally { if(iterProp!=null) iterProp.SetValue(null, saved); CleanupProbe(); }
            _solArea = _probeArea; _solSpeedMs = V; _solReady = _solAoA.Count >= 3;
            Debug.Log($"[AeroSolver] sweep done: {_solAoA.Count} pts, area {_solArea:F2}, surfaces={_probeHasSurfaces}, flap={_flapDeflect*100f:F0}%, ready={_solReady}");
            if (_solReady)
            {
                var cv=new System.Text.StringBuilder("[AeroSolver] curve  α:CL/CD/CM  ");
                for(int i=0;i<_solAoA.Count;i++) cv.Append($"{_solAoA[i]:F0}:{_solCL[i]:F2}/{_solCD[i]:F3}/{_solCM[i]:F3} ");
                Debug.Log(cv.ToString());
            }
        }

        // Re-sweep only when the wing/airfoil/area or test speed changes (sweep is expensive).
        private void MaybeSweep()
        {
            if (_inFlight || !_exactReady) { _solReady=false; return; }
            // CL/CD are ~speed-independent, so the curve is run once at a reference speed and reused
            // (Lift just rescales by q). Re-sweep only when the wing geometry/airfoil changes — DEBOUNCED:
            // a full sweep is 45 solver points, so while the user is dragging a slider or resizing the wing
            // we wait until the value has been stable for a beat instead of sweeping every tick.
            string key = $"{_airfoilName}|{_rep.area:F2}|{_rep.span:F2}|flap{_flapDeflect:F2}|slat{_slatDeflect:F2}";
            if (key == _solKey && _solReady) return;
            if (key != _sweepPendKey) { _sweepPendKey = key; _sweepPendAt = Time.unscaledTime; return; }
            if (Time.unscaledTime - _sweepPendAt < 0.35f) return;     // still settling
            _solKey = key;
            RunSolverSweep(50f);
        }

        private void MaybeRunAutoVerify()
        {
            if (!_autoVerifyPending) return;
            RunAutoVerify(force:false);
        }

        private string AutoVerifyKey()
        {
            return $"{_airfoilName}|{_rep.area:F3}|{_rep.span:F3}|{_rep.chord:F3}|{_liftScale:F3}|{_viscScale:F3}|{_zldScale:F3}|m{EffMassKg:F0}";
        }

        private VerifyResult CaptureVerifyResult(float flap)
        {
            _flapDeflect = flap;
            _solKey = "";
            RunSolverSweep(50f);

            var r = new VerifyResult { flap=flap, area=_solArea, span=_rep.span, ar=WingAR(_rep), points=_solAoA.Count, ready=_solReady };
            if (!_solReady || _solCL.Count == 0) return r;

            r.clMax = -1e9f; r.clMin = 1e9f;
            for (int i=0;i<_solCL.Count;i++)
            {
                float cl=_solCL[i], aoa=_solAoA[i];
                if (cl > r.clMax) { r.clMax=cl; r.stallPos=aoa; }
                if (cl < r.clMin) { r.clMin=cl; r.stallNeg=aoa; }
                if (i < _solCD.Count && _solCD[i] > 1e-5f)
                {
                    float ld = cl / _solCD[i];
                    if (ld > r.ldMax) { r.ldMax=ld; r.ldAoA=aoa; }
                }
            }
            r.clAt0 = InterpCurve(_solAoA, _solCL, 0f);
            r.cdAt0 = InterpCurve(_solAoA, _solCD, 0f);
            r.slope = (InterpCurve(_solAoA,_solCL,2f) - InterpCurve(_solAoA,_solCL,-2f)) / 4f;
            r.zeroLift = CurveZeroCross(_solAoA, _solCL);
            return r;
        }

        private void RunAutoVerify(bool force)
        {
            if (_inFlight || !_exactReady || _wings.Count==0 || _rep.area<0.05f)
            {
                _autoVerifyPending = true;
                if (_verifyText != null) _verifyText.text = "<color=#8899ff>Verify: waiting for selected wing solver data...</color>";
                return;
            }

            string key = AutoVerifyKey();
            if (!force && key == _lastAutoVerifyKey && !string.IsNullOrEmpty(_verifySummary))
            {
                _autoVerifyPending = false;
                if (_verifyText != null) _verifyText.text = _verifySummary;
                return;
            }

            _autoVerifyPending = false;
            _lastAutoVerifyKey = key;
            float savedFlap = _flapDeflect;
            object savedOverride = _probeWingOverride;
            _probeWingOverride = GetToolWing(out _) ?? GetLargestDesignerWing(out _);

            VerifyResult clean = CaptureVerifyResult(0f);
            VerifyResult pos = CaptureVerifyResult(1f);
            VerifyResult neg = CaptureVerifyResult(-1f);
            VerifyResult best = pos.clMax >= neg.clMax ? pos : neg;
            float dCl = best.clMax - clean.clMax;
            float massKg = EffMassKg;

            _verifySummary = $"<color=#aaffdd><b>Verify</b></color>  flap0 CLmax {clean.clMax:F2} @ {clean.stallPos:F0}deg  +100 {pos.clMax:F2}  -100 {neg.clMax:F2}  best {best.flap*100f:F0}% ΔCL {dCl:F2}";
            if (_verifyText != null) _verifyText.text = _verifySummary;

            string wingName = (_rep.name ?? "wing").Replace("\"", "'");
            string foil = (_airfoilName ?? "").Replace("\"", "'");
            Debug.Log($"[AeroAuto] VERIFY wing=\"{wingName}\" foil=\"{foil}\" area={_rep.area:F3} span={_rep.span:F3} ar={WingAR(_rep):F3} massKg={massKg:F1} speedMs=50.0 " +
                      $"flap0={{pts:{clean.points},CLmax:{clean.clMax:F4},stallDeg:{clean.stallPos:F1},CL0:{clean.clAt0:F4},CD0:{clean.cdAt0:F5},slope:{clean.slope:F5},a0:{clean.zeroLift:F2},LDmax:{clean.ldMax:F2},LDAoA:{clean.ldAoA:F1}}} " +
                      $"flapP100={{pts:{pos.points},CLmax:{pos.clMax:F4},stallDeg:{pos.stallPos:F1},CL0:{pos.clAt0:F4},CD0:{pos.cdAt0:F5},slope:{pos.slope:F5},a0:{pos.zeroLift:F2},LDmax:{pos.ldMax:F2},LDAoA:{pos.ldAoA:F1}}} " +
                      $"flapN100={{pts:{neg.points},CLmax:{neg.clMax:F4},stallDeg:{neg.stallPos:F1},CL0:{neg.clAt0:F4},CD0:{neg.cdAt0:F5},slope:{neg.slope:F5},a0:{neg.zeroLift:F2},LDmax:{neg.ldMax:F2},LDAoA:{neg.ldAoA:F1}}} " +
                      $"bestFlapPct={best.flap*100f:F0} deltaCL={dCl:F4}");

            _flapDeflect = savedFlap;
            _probeWingOverride = savedOverride;
            _solKey = "";
            if (_graphImage != null)
            {
                RunSolverSweep(50f);
                RedrawGraph();
                UpdateStats();
            }
        }

        // Linear interpolation in the cached solver curve (clamped at the ends).
        private static float InterpCurve(System.Collections.Generic.List<float> xs, System.Collections.Generic.List<float> ys, float x)
        {
            int nlast = xs.Count-1;
            if (nlast < 1) return 0f;
            if (x <= xs[0]) return ys[0];
            if (x >= xs[nlast]) return ys[nlast];
            for (int i=0;i<nlast;i++) if (x>=xs[i] && x<=xs[i+1])
            { float t=(x-xs[i])/(xs[i+1]-xs[i]); return ys[i]+(ys[i+1]-ys[i])*t; }
            return ys[nlast];
        }
        // AoA where the lift curve crosses zero (zero-lift angle), interpolated.
        private static float CurveZeroCross(System.Collections.Generic.List<float> aoa, System.Collections.Generic.List<float> cl)
        {
            for (int i=0;i<cl.Count-1;i++)
                if ((cl[i]<=0f && cl[i+1]>=0f) || (cl[i]>=0f && cl[i+1]<=0f))
                { float d=cl[i+1]-cl[i]; float t=Mathf.Abs(d)<1e-6f?0f:(0f-cl[i])/d; return aoa[i]+(aoa[i+1]-aoa[i])*t; }
            return 0f;
        }

        private void CycleX(){ PlayClick(); _xAxis=(XAx)(((int)_xAxis+1)%XNames.Length); if(_xBtnLabel)_xBtnLabel.text=$"X: {XNames[(int)_xAxis]}"; UpdateStepperVisibility(); ResetView(); }
        private void CycleY(){ PlayClick(); _yAxis=(YAx)(((int)_yAxis+1)%YNames.Length); if(_yBtnLabel)_yBtnLabel.text=$"Y: {YNames[(int)_yAxis]}"; UpdateStepperVisibility(); ResetView(); }

        // ── Pan / zoom (MATLAB-style) ─────────────────────────────────────────
        private void PanGraph(Vector2 d)
        {
            if(_vxMax<=_vxMin || _vyMax<=_vyMin) return;
            _viewCustom=true;
            float xr=_vxMax-_vxMin, yr=_vyMax-_vyMin;
            float dx = -d.x * xr / GW;          // drag follows data
            float dy = -d.y * yr / GH;
            _vxMin+=dx; _vxMax+=dx; _vyMin+=dy; _vyMax+=dy;
        }
        private void ZoomGraph(float dir)
        {
            if(_vxMax<=_vxMin || _vyMax<=_vyMin || Mathf.Abs(dir)<1e-4f) return;
            _viewCustom=true;
            float f = dir>0f ? 0.8f : 1.25f;   // + zoom in, – zoom out
            float cx=(_vxMin+_vxMax)*0.5f, cy=(_vyMin+_vyMax)*0.5f;
            float hx=(_vxMax-_vxMin)*0.5f*f, hy=(_vyMax-_vyMin)*0.5f*f;
            _vxMin=cx-hx; _vxMax=cx+hx; _vyMin=cy-hy; _vyMax=cy+hy;
        }
        private void ResetView(){ _viewCustom=false; }

        // ── Diagnostic: find where wing geometry lives ────────────────────────
        private void DiagnosticDump()
        {
            var f = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

            // Deep dump JWingScript / WingScript — all members + one level into object refs
            var seen = new HashSet<string>();
            foreach (var mb in Object.FindObjectsByType<MonoBehaviour>(FindObjectsSortMode.None))
            {
                if (mb == null) continue;
                var t = mb.GetType();
                if (t.Name != "JWingScript" && t.Name != "WingScript") continue;
                if (!seen.Add(t.FullName)) continue;
                DumpObject(mb, t, "", 0);
            }

            // Dump AudioManager API (for click sound)
            foreach (var asm in System.AppDomain.CurrentDomain.GetAssemblies())
            {
                var at = asm.GetType("Assets.Scripts.Audio.AudioManager");
                if (at == null) continue;
                Debug.Log($"[AeroDiag] === AudioManager ({at.FullName}) ===");
                foreach (var m in at.GetMethods(BindingFlags.Public|BindingFlags.Static|BindingFlags.NonPublic|BindingFlags.Instance))
                {
                    if (!m.Name.Contains("Play") && !m.Name.Contains("Sound") && !m.Name.Contains("Click")) continue;
                    var ps = string.Join(", ", System.Array.ConvertAll(m.GetParameters(), x=>x.ParameterType.Name+" "+x.Name));
                    Debug.Log($"[AeroDiag]   {(m.IsStatic?"static ":"")}{m.ReturnType.Name} {m.Name}({ps})");
                }
                foreach (var pr in at.GetProperties(BindingFlags.Public|BindingFlags.Static|BindingFlags.NonPublic|BindingFlags.Instance))
                    if (pr.Name.Contains("Instance")||pr.Name.Contains("Store")) Debug.Log($"[AeroDiag]   PROP {pr.PropertyType.Name} {pr.Name}");
                break;
            }

            // Dump a cloned button's ButtonWidget fields (for sound)
            var btn = FindGO("AeroButton");
            if (btn != null)
            {
                foreach (var c in btn.GetComponents<Component>())
                {
                    if (c == null) continue;
                    Debug.Log($"[AeroDiag] BTN-COMP {c.GetType().FullName}");
                    if (c.GetType().Name.Contains("Widget") || c.GetType().Name.Contains("Button"))
                    {
                        foreach (var fi in c.GetType().GetFields(f))
                        {
                            object v=null; try{v=fi.GetValue(c);}catch{}
                            Debug.Log($"[AeroDiag]   BTNF {fi.FieldType.Name} {fi.Name} = {v}");
                        }
                    }
                }
            }
        }

        private void DumpObject(object obj, System.Type t, string indent, int depth)
        {
            if (obj == null || depth > 2) return;
            var f = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            Debug.Log($"[AeroDiag] {indent}=== {t.FullName} ===");
            foreach (var p in t.GetProperties(f))
            {
                if (p.GetIndexParameters().Length>0) continue;
                object v=null; try{v=p.GetValue(obj);}catch{continue;}
                var pt=p.PropertyType;
                if (pt==typeof(float)||pt==typeof(int)||pt==typeof(double))
                { try{ if(Mathf.Abs(System.Convert.ToSingle(v))>0.0001f) Debug.Log($"[AeroDiag] {indent}  PROP {p.Name} = {v}"); }catch{} }
                else if (depth==0 && v!=null && pt.Name.Contains("Data"))
                { Debug.Log($"[AeroDiag] {indent}  →OBJ {p.Name} : {pt.Name}"); DumpObject(v, pt, indent+"    ", depth+1); }
            }
            foreach (var fi in t.GetFields(f))
            {
                object v=null; try{v=fi.GetValue(obj);}catch{continue;}
                var ft=fi.FieldType;
                if (ft==typeof(float)||ft==typeof(int)||ft==typeof(double))
                { try{ if(Mathf.Abs(System.Convert.ToSingle(v))>0.0001f) Debug.Log($"[AeroDiag] {indent}  FIELD {fi.Name} = {v}"); }catch{} }
                else if (depth==0 && v!=null && ft.Name.Contains("Data"))
                { Debug.Log($"[AeroDiag] {indent}  →OBJF {fi.Name} : {ft.Name}"); DumpObject(v, ft, indent+"    ", depth+1); }
            }
        }

        // ── Cache ─────────────────────────────────────────────────────────────
        private void TryBuildCache()
        {
            _cacheTries++;
            var f = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

            // Resolve the JWingTool access chain (Designer.Instance.Tools.JWingTool) — type-based,
            // since none of these are MonoBehaviours.
            if (_toolType == null)
            {
                var dType  = FindType("Assets.Scripts.Design.Designer");
                var dtType = FindType("Assets.Scripts.Design.Tools.DesignerTools");
                var jwtType= FindType("Assets.Scripts.Design.Tools.JWingTool");
                if (dType!=null && dtType!=null && jwtType!=null)
                {
                    _designerInstanceProp = dType.GetProperty("Instance", BindingFlags.Public|BindingFlags.Static|BindingFlags.NonPublic);
                    _toolsProp     = dType.GetProperty("Tools", f);
                    _jwingToolProp = dtType.GetProperty("JWingTool", f);
                    // Craft total mass: prefer Aircraft.GetStats(LoadedWeight), which applies SP2's
                    // user-facing mass scale. CenterOfMass.LoadedMass is the raw physics mass.
                    _dAircraftProp = dType.GetProperty("Aircraft", f);
                    var acT = _dAircraftProp?.PropertyType;
                    _acComProp = acT?.GetProperty("CenterOfMass", f);
                    _comLoadedMassProp = _acComProp?.PropertyType?.GetProperty("LoadedMass", f);
                    _toolType      = jwtType;
                    _tCurWing      = jwtType.GetProperty("CurrentWing", f);
                    _tSliceAirfoil = jwtType.GetProperty("SliceAirfoil", f);
                    var dt = _tCurWing?.PropertyType;  // JWingData
                    if (dt != null)
                    {
                        _jdArea2     = dt.GetProperty("WingArea",f);
                        _jdSpan2     = dt.GetProperty("WingSpan",f);
                        _jdLiftScale = dt.GetProperty("LiftScale",f);
                        _jdViscScale = dt.GetProperty("ViscousDragScale",f);
                        _jdZldScale  = dt.GetProperty("ZeroLiftDragScale",f);
                        _jdSlices    = dt.GetProperty("WingSlices",f);
                        var st = _jdSlices?.PropertyType;
                        var et = st!=null && st.IsGenericType ? st.GetGenericArguments()[0] : null;
                        if (et != null) _iwsAirfoil = et.GetProperty("Airfoil",f);
                    }
                }
            }
            foreach (var mb in Object.FindObjectsByType<MonoBehaviour>(FindObjectsSortMode.None))
            {
                if (mb==null) continue;
                var tn = mb.GetType().Name;
                if (tn=="WingPhysicsScript" && _wpsType==null)
                {
                    var t=_wpsType=mb.GetType();
                    _pCL=t.GetProperty("CoeffecientOfLift",f); _pCD=t.GetProperty("CoeffecientOfDrag",f);
                    _pLiftN=t.GetProperty("LiftForceMagnitude",f); _pDragN=t.GetProperty("DragForceMagnitude",f);
                    _pAoA=t.GetProperty("AngleOfAttack",f)??t.GetProperty("AoA",f);
                    _pArea=t.GetProperty("WingArea",f); _pSpan=t.GetProperty("WingSpan",f);
                    _pChord=t.GetProperty("Chord",f); _pThick=t.GetProperty("WingThickness",f);
                    _pCamber=t.GetProperty("camberAmount",f)??t.GetProperty("CamberAmount",f);
                    _pPre=t.GetProperty("PrecomputedLift",f);
                    if(_pPre!=null){_preType=_pPre.PropertyType;_fLG=_preType.GetField("liftGradient",f);_fSP=_preType.GetField("stallPositive",f);_fSN=_preType.GetField("stallNegative",f);_fCD0=_preType.GetField("zeroLiftDrag",f);}
                }
                if (tn=="JWingScript" && _wsType==null)
                {
                    var t=_wsType=mb.GetType();
                    _wsDataProp = t.GetProperty("Data",f);
                    _wsPhysicsProp = t.GetProperty("Physics",f);
                    if (_wsDataProp != null)
                    {
                        var dt = _wsDataProp.PropertyType; // JWingData
                        _jdArea = dt.GetProperty("WingArea",f);
                        _jdSpan = dt.GetProperty("WingSpan",f);
                        _jdMass = dt.GetProperty("Mass",f);
                        if (_jdArea2==null)     _jdArea2     = dt.GetProperty("WingArea",f);
                        if (_jdSpan2==null)     _jdSpan2     = dt.GetProperty("WingSpan",f);
                        if (_jdLiftScale==null) _jdLiftScale = dt.GetProperty("LiftScale",f);
                        if (_jdViscScale==null) _jdViscScale = dt.GetProperty("ViscousDragScale",f);
                        if (_jdZldScale==null)  _jdZldScale  = dt.GetProperty("ZeroLiftDragScale",f);
                        if (_jdSlices==null)    _jdSlices    = dt.GetProperty("WingSlices",f);
                        var st = _jdSlices?.PropertyType;
                        var et = st!=null && st.IsGenericType ? st.GetGenericArguments()[0] : null;
                        if (et != null && _iwsAirfoil==null) _iwsAirfoil = et.GetProperty("Airfoil",f);
                    }
                }
            }
            if(_wpsType!=null||_wsType!=null)_cacheReady=true;
        }

        // "Are we actually in the flight scene?" — asked of SP2's SceneManager, which is authoritative.
        // We can't trust FlightSceneScript.Instance: even with a Unity-null check, the destroyed singleton (or
        // its LocalPlayer, a non-Unity object that C# null-checks can't see through) lingers after returning to
        // the editor, leaving the panel stuck thinking it's flying ("No wing physics."). The scene flag flips
        // back cleanly. Falls back to the Unity-null FlightSceneScript check only if the SceneManager is absent.
        private System.Type _gameTypeCache;
        private bool IsInFlight()
        {
            try {
                if (_gameTypeCache==null) _gameTypeCache = FindType("Assets.Scripts.Game");
                object game = _gameTypeCache?.GetProperty("Instance", BindingFlags.Public|BindingFlags.Static|BindingFlags.NonPublic)?.GetValue(null);
                object sm = game?.GetType().GetProperty("SceneManager")?.GetValue(game);
                if (sm!=null)
                {
                    object inFlight = sm.GetType().GetProperty("InFlightScene")?.GetValue(sm);
                    if (inFlight is bool b)
                    {
                        // SP2's IN-FLIGHT designer (wrench button in flight) edits the craft WITHOUT leaving
                        // the Terrain scene — InFlightScene stays true there, but it IS a designer context.
                        bool fd=false; try { fd=(bool)(sm.GetType().GetProperty("InFlightDesigner")?.GetValue(sm) ?? false); } catch {}
                        return b && !fd;
                    }
                }
            } catch {}
            var fs = FlightSceneScript.Instance;             // fallback (Unity-null-safe)
            return fs != null && fs.LocalPlayer != null;
        }

        // ── Data ──────────────────────────────────────────────────────────────
        private void RefreshData()
        {
            _wings.Clear(); _totalLiftN=_totalDragN=0; _repFromDesigner=false; _exactReady=false; _polarValid=false;
            _inFlight = IsInFlight();
            if (_inFlight)
            {
                var rb=FlightSceneScript.Instance.LocalPlayer.Aircraft?.GetComponentInChildren<Rigidbody>();
                if(rb!=null){_speedMs=rb.linearVelocity.magnitude;_altM=rb.position.y;_dynPa=0.5f*ISA(_altM)*_speedMs*_speedMs;}
            }
            if(_wpsType!=null && _inFlight)   // live flight physics — never in a designer context (incl. in-flight designer)
            {
                foreach(var mb in Object.FindObjectsByType<MonoBehaviour>(FindObjectsSortMode.None))
                {
                    if(mb?.GetType()!=_wpsType)continue;
                    var s=new WingSnap{name=mb.gameObject.name,cl=PF(mb,_pCL),cd=PF(mb,_pCD),liftN=PF(mb,_pLiftN),dragN=PF(mb,_pDragN),aoa=PF(mb,_pAoA)*Mathf.Rad2Deg,area=PF(mb,_pArea),span=PF(mb,_pSpan),chord=PF(mb,_pChord),thickness=PF(mb,_pThick),camber=PF(mb,_pCamber)};
                    if(_pPre!=null){try{var p=_pPre.GetValue(mb);if(p!=null){s.liftGradient=FF(p,_fLG);s.stallPos=FF(p,_fSP)*Mathf.Rad2Deg;s.stallNeg=FF(p,_fSN)*Mathf.Rad2Deg;s.cd0=FF(p,_fCD0);s.hasAero=true;s.fromSp2Model=true;}}catch{}}
                    _totalLiftN+=s.liftN;_totalDragN+=s.dragN;_wings.Add(s);
                }
            }
            // Designer: read SP2's own displayed values straight off the UI text
            if(_wings.Count==0 && !_inFlight)
            {
                ReadDesignerFromUI();
            }

            // Total area always sums every piece (whole aircraft).
            _totalAreaAll = 0f;
            foreach (var ww in _wings) _totalAreaAll += ww.area;

            // Representative wing: in the designer, ReadDesignerFromUI already set _rep to the
            // SELECTED wing (from SP2's STATISTICS panel). Otherwise pick the largest piece.
            if (!_repFromDesigner && _wings.Count > 0)
            {
                _rep = _wings[0];
                foreach (var ww in _wings) if (ww.area > _rep.area) _rep = ww;
            }

            _scopeArea = _inFlight ? _rep.area : ComputeScopeArea();   // mirrored-wing / all-lifting area for the scope
        }
        private float _vfTimer;
        private bool _quickFlightStarted, _quickFlightDone, _quickFlightLifted;
        private int _quickCaseIndex, _quickSettleFrames, _quickSamples, _quickTotalSamples;
        private float _quickCaseClAbs, _quickCaseCdAbs, _quickCaseClMax, _quickCaseCdMax;
        private float _quickAllClAbs, _quickAllCdAbs, _quickAllClMax, _quickAllCdMax, _quickElapsed;
        private struct QuickCase { public float flap, aoa, speed; public QuickCase(float f,float a,float s){flap=f;aoa=a;speed=s;} }
        private static readonly QuickCase[] QuickCases = {
            new QuickCase(0f, 0f, 50f), new QuickCase(0f, 4f, 50f), new QuickCase(0f, 8f, 50f), new QuickCase(0f, 12f, 50f), new QuickCase(0f, 14f, 50f),
            new QuickCase(1f, 0f, 50f), new QuickCase(1f, 4f, 50f), new QuickCase(1f, 8f, 50f), new QuickCase(1f, 12f, 50f), new QuickCase(1f, 16f, 50f)
        };

        // VERIFICATION: log the live solver's per-wing CL/CD/α/speed (runs in flight regardless of the
        // designer panel) so it can be cross-checked against the designer-predicted curve. Independent 1:1.
        // Real-flight autopilot: spawn airborne with forward speed (gravity ON), apply thrust, hold wings
        // level, gently ramp AoA via the elevator, and sample live CL/CD vs the editor probe. No UFO.
        private int _apPhase, _apPass; private float _apTimer, _apSampleTimer, _apFlap, _apTargetAlt, _apMaxCl, _quickStartSettle;
        private void RunQuickFlightTest()
        {
            if (_quickFlightDone || _wsType==null || _wsPhysicsProp==null) return;
            var ac = FlightSceneScript.Instance?.LocalPlayer?.Aircraft;
            if (ac == null) return;
            var go = ((Component)ac).gameObject; var tr = ((Component)ac).transform;
            var rb0 = go.GetComponentInChildren<Rigidbody>();

            // CHEAP per-frame AoA/speed from the rigidbody (the full-scene wing scan only happens at the
            // 0.4 s sample tick — doing it every frame tanked the framerate to a near-hang).
            Vector3 vel = rb0!=null ? rb0.linearVelocity : Vector3.zero;
            float liveSpd = vel.magnitude;
            Vector3 lv = tr.InverseTransformDirection(vel);
            float liveAoa = (lv.z>0.5f) ? Mathf.Atan2(-lv.y, lv.z)*Mathf.Rad2Deg : 0f;
            bool haveLive = rb0!=null && liveSpd>1f;

            if (!_quickFlightStarted)
            {
                // Wait until the craft is FULLY loaded before taking over. The craft spawns STATIONARY on
                // the ground (speed≈0), so don't gate on speed — gate on the rigidbody existing + a settle
                // delay long enough to clear SP2's craft-load determinism pass (acting during it hangs).
                if (rb0==null) { _quickStartSettle=0f; return; }
                _quickStartSettle += Time.deltaTime;
                if (_quickStartSettle < 4f) return;
                _quickFlightStarted=true; _apPhase=0; _apPass=0; _apTimer=0f; _apSampleTimer=0f; _apFlap=0f; _apMaxCl=0f; _quickTotalSamples=0; _quickElapsed=0f;
                _quickAllClAbs=_quickAllCdAbs=_quickAllClMax=_quickAllCdMax=0f;
                Debug.Log("[AeroQuick] START autopilot: altitude-hold decel sweep to stall, clean then flap. Gravity ON.");
            }

            if (_apPhase==0)   // spawn airborne with forward velocity
            {
                tr.position += Vector3.up * 1500f; _apTargetAlt = tr.position.y;
                Vector3 fwd = tr.forward; fwd.y=0f; if(fwd.sqrMagnitude<0.01f) fwd=Vector3.forward; fwd.Normalize();
                foreach (var rb in go.GetComponentsInChildren<Rigidbody>()){ rb.useGravity=true; rb.linearVelocity=fwd*55f; rb.angularVelocity=Vector3.zero; }
                _apPhase=1; _apTimer=0f;
                Debug.Log("[AeroQuick] airborne @ +1500m, 55 m/s forward");
                return;
            }

            _apTimer += Time.deltaTime; _quickElapsed += Time.deltaTime;
            float alt = tr.position.y, vy = rb0!=null?rb0.linearVelocity.y:0f;
            if (alt < 250f) { _quickFlightDone=true; Debug.Log($"[AeroQuick] ABORT (alt {alt:F0}m). samples={_quickTotalSamples}"); FinishQuickSummary(); return; }
            // Global watchdog: never let the rig run forever (underpowered/odd craft) — finish with what we have.
            if (_quickElapsed > 360f) { _quickFlightDone=true; Debug.Log($"[AeroQuick] TIMEOUT after {_quickElapsed:F0}s. samples={_quickTotalSamples}"); FinishQuickSummary(); return; }

            // Wings level + ALTITUDE HOLD via elevator (SP2 Pitch: + = nose down).
            float bankDeg = Mathf.Asin(Mathf.Clamp(tr.right.y,-1f,1f))*Mathf.Rad2Deg;
            SetControlValue(ac, "Roll", Mathf.Clamp(-0.03f*bankDeg, -0.6f, 0.6f));
            SetControlValue(ac, "Yaw", 0f); SetControlValue(ac, "Brake", 0f); SetControlValue(ac, "Flaps", _apFlap);
            float pitchCmd = Mathf.Clamp(0.004f*(alt-_apTargetAlt) + 0.05f*vy, -0.7f, 0.7f);   // hold altitude → AoA rises as it slows
            SetControlValue(ac, "Pitch", pitchCmd);

            bool building = (_apPhase==1 || _apPhase==3);          // building speed/altitude (full power)
            SetControlValue(ac, "Throttle", building ? 1f : 0.12f);
            SetControlValue(ac, "Vtol",     building ? 0.6f : 0.15f);

            if (_apPhase==1 || _apPhase==3)   // stabilize / recover
            {
                if (_apTimer>4f && haveLive && liveSpd>48f && Mathf.Abs(vy)<6f)
                { _apPhase=2; _apTimer=0f; _apSampleTimer=0f; _apMaxCl=0f; Debug.Log($"[AeroQuick] stabilized → sweep (pass {_apPass}, flap {_apFlap*100f:F0}%)"); }
                // Watchdog: an underpowered craft may never sustain 48 m/s. Re-throw it to flying speed and
                // force the sweep — the decel sweep itself needs no thrust, just initial airspeed.
                else if (_apTimer>35f)
                {
                    Vector3 fwd2 = tr.forward; fwd2.y=0f; if(fwd2.sqrMagnitude<0.01f) fwd2=Vector3.forward; fwd2.Normalize();
                    foreach (var rb in go.GetComponentsInChildren<Rigidbody>()){ rb.linearVelocity=fwd2*55f; rb.angularVelocity=Vector3.zero; }
                    _apTargetAlt = tr.position.y;
                    _apPhase=2; _apTimer=0f; _apSampleTimer=0f; _apMaxCl=0f;
                    Debug.Log($"[AeroQuick] stabilize watchdog → re-thrown to 55 m/s, forcing sweep (pass {_apPass})");
                }
                return;
            }

            // Phase 2 — decelerating altitude-hold; AoA rises to stall. Sample live vs probe.
            _apMaxCl = Mathf.Max(_apMaxCl, liveAoa);                 // _apMaxCl reused as max AoA seen
            bool stalled = liveSpd<20f || (liveAoa>10f && liveAoa < _apMaxCl-2.5f);
            if (stalled || _apTimer>32f)
            {
                Debug.Log($"[AeroQuick] sweep end (pass {_apPass}) maxAoA={_apMaxCl:F1}");
                if (_apPass==0) { _apPass=1; _apFlap=-1f; _apPhase=3; _apTimer=0f; Debug.Log("[AeroQuick] → flap pass: Flaps -100% (this craft deploys on negative)"); return; }
                _quickFlightDone=true; FinishQuickSummary(); return;
            }
            _apSampleTimer += Time.deltaTime;
            if (_apSampleTimer>=0.4f && haveLive && liveSpd>19f && Mathf.Abs(bankDeg)<20f)
            {
                _apSampleTimer=0f;
                if (TryLiveProbeCompare(out float la, out float ls, out float lf, out float cl, out float cd, out float pcl, out float pcd))
                {
                    float dcl=cl-pcl, dcd=cd-pcd, adcl=Mathf.Abs(dcl), adcd=Mathf.Abs(dcd);
                    _quickTotalSamples++; _quickAllClAbs+=adcl; _quickAllCdAbs+=adcd; _quickAllClMax=Mathf.Max(_quickAllClMax,adcl); _quickAllCdMax=Mathf.Max(_quickAllCdMax,adcd);
                    Debug.Log($"[AeroQuick] sample pass={_apPass} aoa={la:F2} spd={ls*1.944f:F0}kts flap={lf:F2} liveCL={cl:F4} probeCL={pcl:F4} dCL={dcl:+0.0000;-0.0000;0.0000} liveCD={cd:F5} probeCD={pcd:F5} dCD={dcd:+0.00000;-0.00000;0.00000}");
                }
            }
        }
        private void FinishQuickSummary(){ float n=Mathf.Max(1,_quickTotalSamples); Debug.Log($"[AeroQuick] DONE samples={_quickTotalSamples} avgAbsCL={_quickAllClAbs/n:F4} maxAbsCL={_quickAllClMax:F4} avgAbsCD={_quickAllCdAbs/n:F5} maxAbsCD={_quickAllCdMax:F5}"); }

        private void SetControlValue(object aircraft, string prop, float value)
        {
            try
            {
                object controls = aircraft?.GetType().GetProperty("Controls")?.GetValue(aircraft);
                var p = controls?.GetType().GetProperty(prop);
                if (p != null && p.CanWrite) p.SetValue(controls, value);
            }
            catch {}
        }

        private bool TryLiveProbeCompare(out float aoa, out float spd, out float flaps, out float cl, out float cd, out float pcl, out float pcd)
        {
            aoa=spd=flaps=cl=cd=pcl=pcd=0f;
            if (!TryGetLiveWingSample(out object data, out aoa, out spd, out flaps, out cl, out cd, out _)) return false;
            if (data==null || spd<5f || Mathf.Abs(aoa)>60f) return false;
            object savedOverride=_probeWingOverride; float savedFlap=_flapDeflect; var savedRep=_rep;
            try {
                _probeWingOverride=data; _flapDeflect=flaps; _useLiveInputs=true;   // replay live surface deflections in the probe
                float area=PF(data,_jdArea), span=PF(data,_jdSpan);
                _rep = new WingSnap { name="Live wing", area=area, span=span, chord=area/Mathf.Max(0.01f,span), hasAero=true };
                if (!BuildProbeWing()) return false;
                // Match the sweep's convergence (default 10 iters/call is under-converged for a one-shot).
                var llsType=FindType("Assets.Scripts.Craft.Wings.Physics.LiftingLineSolver");
                var iterProp=llsType?.GetProperty("IterationsSetting", BindingFlags.Public|BindingFlags.Static);
                int savedIters=iterProp!=null?(int)iterProp.GetValue(null):10;
                try { iterProp?.SetValue(null, 80); return RunSolverPoint(spd, aoa, 24, out pcl, out pcd, out _); }
                finally { if(iterProp!=null) iterProp.SetValue(null, savedIters); }
            }
            finally { CleanupProbe(); _probeWingOverride=savedOverride; _flapDeflect=savedFlap; _rep=savedRep; _useLiveInputs=false; }
        }

        private bool TryGetLiveWingSample(out object bestData, out float aoa, out float spd, out float flaps, out float cl, out float cd, out float liftN)
        {
            bestData=null; aoa=spd=flaps=cl=cd=liftN=0f;
            object bestMgr=null; float bestArea=0f;
            foreach (var mb in Object.FindObjectsByType<MonoBehaviour>(FindObjectsSortMode.None))
            {
                if (mb?.GetType()!=_wsType) continue;
                object mgr=null, data=null; try{ mgr=_wsPhysicsProp.GetValue(mb); data=_wsDataProp.GetValue(mb); }catch{}
                if (mgr==null||data==null) continue;
                float area=PF(data,_jdArea); if(area>bestArea){bestArea=area;bestMgr=mgr;bestData=data;}
            }
            if (bestMgr==null || bestArea<0.05f) return false;
            var mt=bestMgr.GetType();
            try { mt.GetProperty("DebugEnable")?.SetValue(bestMgr,true); } catch {}
            // Capture the live craft's evaluated control-surface inputs (for the probe to replay).
            try { var imF=mt.GetField("_inputManager", BindingFlags.NonPublic|BindingFlags.Instance);
                  object im=imF?.GetValue(bestMgr); object ins=im?.GetType().GetProperty("Inputs")?.GetValue(im);
                  var arr=NativeFloatArray(ins); if(arr!=null) _liveInputs=arr; } catch {}
            object dbg=null; try{ dbg=mt.GetMethod("GetDebugData")?.Invoke(bestMgr,null); }catch{}
            if (dbg==null) return false;
            var hv=dbg.GetType().GetProperty("HasValue"); if(hv!=null && !(bool)hv.GetValue(dbg)) return false;
            object na=dbg.GetType().GetProperty("Value")?.GetValue(dbg) ?? dbg;
            int len=(int)na.GetType().GetProperty("Length").GetValue(na); var item=na.GetType().GetMethod("get_Item");
            Vector3 liftV=Vector3.zero, dragV=Vector3.zero;
            for(int i=0;i<len;i++){ object d=item.Invoke(na,new object[]{i});
                liftV+=F3vec(d.GetType().GetField("liftForce").GetValue(d));
                dragV+=F3vec(d.GetType().GetField("dragForce").GetValue(d)); }
            var ap=mt.GetProperty("SliceAeroData");
            if(ap!=null){ object aa=ap.GetValue(bestMgr); int al=(int)aa.GetType().GetProperty("Length").GetValue(aa);
                if(al>0){ object sd=aa.GetType().GetMethod("get_Item").Invoke(aa,new object[]{al/2});
                    aoa=GetF(sd,"alpha")*Mathf.Rad2Deg; spd=GetF(sd,"freeStreamSpeed"); } }
            float rho=1.225f;
            var wp=mt.GetProperty("WingInputData");
            if(wp!=null){ object wa=wp.GetValue(bestMgr); int wl=(int)wa.GetType().GetProperty("Length").GetValue(wa);
                if(wl>0){ object wid=wa.GetType().GetMethod("get_Item").Invoke(wa,new object[]{0});
                    object atmo=wid.GetType().GetField("atmosphere")?.GetValue(wid);
                    float d=atmo!=null?GetF(atmo,"density"):0f; if(d>0.01f) rho=d; } }
            if (spd<2f) return false;
            float q=0.5f*rho*spd*spd;
            liftN = liftV.magnitude;
            cl=liftV.magnitude/(q*bestArea); cd=dragV.magnitude/(q*bestArea);
            flaps=LiveControlValue("Flaps");
            return true;
        }

        private float LiveControlValue(string name)
        {
            try
            {
                object aircraft = FlightSceneScript.Instance?.LocalPlayer?.Aircraft;
                object controls = aircraft?.GetType().GetProperty("Controls")?.GetValue(aircraft);
                object value = controls?.GetType().GetProperty(name)?.GetValue(controls);
                return value == null ? 0f : System.Convert.ToSingle(value);
            }
            catch { return 0f; }
        }

        private void LogFlightVerify()
        {
            // Largest live wing's manager (the main wing).
            object bestMgr=null, bestData=null; float bestArea=0f;
            foreach (var mb in Object.FindObjectsByType<MonoBehaviour>(FindObjectsSortMode.None))
            {
                if (mb?.GetType()!=_wsType) continue;
                object mgr=null, data=null; try{ mgr=_wsPhysicsProp.GetValue(mb); data=_wsDataProp.GetValue(mb); }catch{}
                if (mgr==null||data==null) continue;
                float area=PF(data,_jdArea); if(area>bestArea){bestArea=area;bestMgr=mgr;bestData=data;}
            }
            if (bestMgr==null || bestArea<0.05f) return;
            var mt=bestMgr.GetType();
            try { mt.GetProperty("DebugEnable")?.SetValue(bestMgr,true); } catch {}   // off in flight by default

            object dbg=null; try{ dbg=mt.GetMethod("GetDebugData")?.Invoke(bestMgr,null); }catch{}
            if (dbg==null) return;                                  // enabled this frame; data ready next poll
            var hv=dbg.GetType().GetProperty("HasValue"); if(hv!=null && !(bool)hv.GetValue(dbg)) return;
            object na=dbg.GetType().GetProperty("Value")?.GetValue(dbg) ?? dbg;
            int len=(int)na.GetType().GetProperty("Length").GetValue(na); var item=na.GetType().GetMethod("get_Item");
            Vector3 liftV=Vector3.zero, dragV=Vector3.zero;
            for(int i=0;i<len;i++){ object d=item.Invoke(na,new object[]{i});
                liftV+=F3vec(d.GetType().GetField("liftForce").GetValue(d));
                dragV+=F3vec(d.GetType().GetField("dragForce").GetValue(d)); }

            float aoa=0f, spd=0f;                                   // mid-slice α & airspeed
            var ap=mt.GetProperty("SliceAeroData");
            if(ap!=null){ object aa=ap.GetValue(bestMgr); int al=(int)aa.GetType().GetProperty("Length").GetValue(aa);
                if(al>0){ object sd=aa.GetType().GetMethod("get_Item").Invoke(aa,new object[]{al/2});
                    aoa=GetF(sd,"alpha")*Mathf.Rad2Deg; spd=GetF(sd,"freeStreamSpeed"); } }   // geometric α (matches designer X)
            float rho=1.225f;                                       // actual density the solver used
            var wp=mt.GetProperty("WingInputData");
            if(wp!=null){ object wa=wp.GetValue(bestMgr); int wl=(int)wa.GetType().GetProperty("Length").GetValue(wa);
                if(wl>0){ object wid=wa.GetType().GetMethod("get_Item").Invoke(wa,new object[]{0});
                    object atmo=wid.GetType().GetField("atmosphere")?.GetValue(wid);
                    float d=atmo!=null?GetF(atmo,"density"):0f; if(d>0.01f) rho=d; } }
            if (spd<2f) return;
            float q=0.5f*rho*spd*spd; float cl=liftV.magnitude/(q*bestArea); float cd=dragV.magnitude/(q*bestArea);
            float flaps = LiveControlValue("Flaps");
            float altM = FlightSceneScript.Instance?.LocalPlayer?.Aircraft?.transform.position.y ?? 0f;
            string cmp = "";
            if (bestData!=null && spd>10f && Mathf.Abs(aoa)<60f)
            {
                object savedOverride=_probeWingOverride; float savedFlap=_flapDeflect; var savedRep=_rep;
                try {
                    _probeWingOverride=bestData; _flapDeflect=flaps;
                    float span=PF(bestData,_jdSpan);
                    _rep = new WingSnap { name="Live wing", area=bestArea, span=span, chord=bestArea/Mathf.Max(0.01f,span), hasAero=true };
                    if (BuildProbeWing() && RunSolverPoint(spd, aoa, 12, out float pcl, out float pcd, out _))
                        cmp = $"  probeCL {pcl:F3} dCL {(cl-pcl):+0.000;-0.000;0.000}  probeCD {pcd:F3} dCD {(cd-pcd):+0.000;-0.000;0.000}";
                } catch (System.Exception e) { cmp = $"  probe EXC {e.GetType().Name}"; }
                finally { CleanupProbe(); _probeWingOverride=savedOverride; _flapDeflect=savedFlap; _rep=savedRep; }
            }
            Debug.Log($"[AeroVerify] LIVE  α {aoa:F1}°  spd {spd*1.944f:F0}kts  flap {flaps:F2}  CL {cl:F3}  CD {cd:F3}{cmp}  lift {liftV.magnitude:F0}N  S {bestArea:F1}  rho {rho:F3}  alt {altM:F0}m");
        }
        private static Vector3 F3vec(object f3)
        {
            if(f3==null) return Vector3.zero; var t=f3.GetType();
            return new Vector3(System.Convert.ToSingle(t.GetField("x").GetValue(f3)),
                               System.Convert.ToSingle(t.GetField("y").GetValue(f3)),
                               System.Convert.ToSingle(t.GetField("z").GetValue(f3)));
        }

        // Finite-wing aspect ratio of a wing piece
        private static float WingAR(WingSnap w) => (w.area>0.05f && w.span>0.05f) ? Mathf.Clamp(w.span*w.span/w.area, 0.5f, 40f) : 6f;

        // Effective 3-D lift-curve slope (per degree) for this wing's airfoil + AR
        private static float LiftSlopePerDeg(WingSnap w)
        {
            float tc = w.thickness>0?w.thickness:0.12f;
            float a2d = 2f*Mathf.PI*(1f+0.77f*tc);                 // 2-D slope, per rad
            float ar  = WingAR(w);
            float a3d = a2d/(1f + a2d/(Mathf.PI*ar*0.95f));        // finite-wing correction
            return a3d*Mathf.Deg2Rad;                               // per degree
        }

        // Zero-lift angle (deg) from camber (NACA-style: ≈ -1.1° per 1% camber)
        private static float ZeroLiftDeg(WingSnap w)
        {
            // Camber position shifts the zero-lift angle: aft camber → more negative α₀.
            // Normalised so p=0.4 (the usual default) reproduces the −110·camber rule of thumb.
            float p = w.camberPos > 0.01f ? Mathf.Clamp(w.camberPos, 0.05f, 0.95f) : 0.4f;
            return -110f * w.camber * (0.5f + p) / 0.9f;
        }

        // Effective lift-curve slope (per DEGREE). In flight, prefer SP2's own
        // PrecomputedLift.liftGradient; in the designer, fall back to thin-airfoil theory.
        // SP2's unit convention is inferred from magnitude: per-radian slopes are O(3–7),
        // per-degree slopes are O(0.05–0.12).
        private static float EffSlopePerDeg(WingSnap w)
        {
            if (w.fromSp2Model && Mathf.Abs(w.liftGradient) > 1e-4f)
            {
                float g = w.liftGradient;
                return Mathf.Abs(g) > 1f ? g * Mathf.Deg2Rad : g;   // per-rad → per-deg, else already per-deg
            }
            return LiftSlopePerDeg(w);
        }

        // Effective zero-lift (parasitic) drag coefficient. Prefer SP2's value in flight.
        private static float EffCd0(WingSnap w)
        {
            if (w.fromSp2Model && w.cd0 > 1e-5f) return w.cd0;
            float tc = w.thickness>0?w.thickness:0.12f;
            return 0.008f + 0.06f*tc;
        }

        // Designer: SELECTED wing + its airfoil come straight from SP2's JWingTool (robust,
        // tracks clicks). Sliders/UI text are only a fallback. Whole-aircraft pieces come from
        // every JWingScript (for the totals line).
        private void ReadDesignerFromUI()
        {
            // Slider-derived airfoil (fallback / for the estimate model)
            float camberH= FindUIValue("Camber Height");
            float camberO= FindUIValue("Camber Offset");
            float thick  = FindUIValue("Thickness");
            float m  = camberH > 0 ? camberH/100f : 0f;
            float p  = camberO > 0 ? Mathf.Clamp(camberO/10f, 0.05f, 0.95f) : 0.4f;
            float tc = thick   > 0 ? thick/100f   : 0.12f;
            float a0PerRad = 2f * Mathf.PI * (1f + 0.77f * tc);
            float stallPos = 16f - 30f * m, stallNeg = -14f + 20f * m;
            _airfoilName = ""; _liftScale = _viscScale = _zldScale = 1f;

            // Whole-aircraft pieces (for the totals line)
            if (_wsType != null && _wsDataProp != null)
            {
                foreach (var mb in Object.FindObjectsByType<MonoBehaviour>(FindObjectsSortMode.None))
                {
                    if (mb?.GetType() != _wsType) continue;
                    object data = null; try { data = _wsDataProp.GetValue(mb); } catch {}
                    if (data == null) continue;
                    float area = PF(data, _jdArea), span = PF(data, _jdSpan);
                    if (area < 0.001f && span < 0.001f) continue;
                    _wings.Add(new WingSnap { name=mb.gameObject.name, area=area, span=span,
                        chord = span>0.01f?area/span:0f, thickness=tc, camber=m, camberPos=p,
                        liftGradient=a0PerRad, stallPos=stallPos, stallNeg=stallNeg, hasAero=true });
                }
            }
            if (_wings.Count == 0)
                _wings.Add(new WingSnap { name="Wing", thickness=tc, camber=m, camberPos=p,
                    liftGradient=a0PerRad, stallPos=stallPos, stallNeg=stallNeg, hasAero=true });

            // Preferred source: the wing the user has SELECTED in the editor (JWingTool).
            // Headless fallback: largest live JWingScript data, so automation can verify without UI clicks.
            object toolWing = GetToolWing(out string airfoil);
            if (toolWing == null) toolWing = GetLargestDesignerWing(out airfoil);
            if (toolWing != null)
            {
                float wArea = PF(toolWing, _jdArea2), wSpan = PF(toolWing, _jdSpan2);
                _liftScale = SaneScale(PF(toolWing, _jdLiftScale));
                _viscScale = SaneScale(PF(toolWing, _jdViscScale));
                _zldScale  = SaneScale(PF(toolWing, _jdZldScale));
                if (string.IsNullOrEmpty(airfoil)) airfoil = FirstSliceAirfoil(toolWing);
                var mr = string.IsNullOrEmpty(airfoil) ? System.Text.RegularExpressions.Match.Empty : _nacaRx.Match(airfoil);
                if (mr.Success)
                {
                    int a=Mathf.Clamp(int.Parse(mr.Groups[1].Value),0,9);
                    int b=Mathf.Clamp(int.Parse(mr.Groups[2].Value),2,7);
                    int cd=Mathf.Clamp(int.Parse(mr.Groups[3].Value),4,24);
                    _exCamberH=a*0.01f; _exCamberPos=b*0.1f; _exTc=cd*0.01f;
                    _std=ComputeStdParams(_exCamberH,_exCamberPos,_exTc);
                    _exactReady=true; _polarValid=false;
                    _airfoilName=$"NACA {a}{b}{cd:00}";
                    m=_exCamberH; p=_exCamberPos; tc=_exTc;   // sync display
                }
                if (wArea>0.001f || wSpan>0.001f)
                {
                    _rep = new WingSnap { name="Selected wing", area=wArea, span=wSpan,
                        chord = wSpan>0.01f?wArea/wSpan:0f, thickness=tc, camber=m, camberPos=p,
                        liftGradient=a0PerRad, stallPos=stallPos, stallNeg=stallNeg, hasAero=true };
                    _repFromDesigner=true;
                }
            }

            // Fallback exact: reconstruct NACA digits from Advanced-mode sliders
            if (!_exactReady && thick > 0f)
            {
                int a=(int)Mathf.Clamp(Mathf.Round(camberH),0f,9f);
                int b=(int)Mathf.Clamp(Mathf.Round(camberO),2f,7f);
                int cd=(int)Mathf.Clamp(Mathf.Round(thick),4f,24f);
                _exCamberH=a*0.01f; _exCamberPos=b*0.1f; _exTc=cd*0.01f;
                _std=ComputeStdParams(_exCamberH,_exCamberPos,_exTc);
                _exactReady=true; _polarValid=false; _airfoilName=$"NACA {a}{b}{cd:00}";
            }

            // Fallback geometry: SP2 STATISTICS panel text
            if (!_repFromDesigner)
            {
                float selArea=FindUIValue("Wing Area"), selSpan=FindUIValue("Wingspan");
                if (selArea>0.001f || selSpan>0.001f)
                {
                    _rep = new WingSnap { name="Selected wing", area=selArea, span=selSpan,
                        chord = selSpan>0.01f?selArea/selSpan:0f, thickness=tc, camber=m, camberPos=p,
                        liftGradient=a0PerRad, stallPos=stallPos, stallNeg=stallNeg, hasAero=true };
                    _repFromDesigner=true;
                }
            }
        }
        private bool _repFromDesigner;

        // The JWingData the user currently has selected, plus its (selected slice's) airfoil string.
        private object GetToolWing(out string airfoil)
        {
            airfoil = null;
            if (_designerInstanceProp==null || _toolsProp==null || _jwingToolProp==null || _tCurWing==null) return null;
            try
            {
                object designer = _designerInstanceProp.GetValue(null);   // Designer.Instance (static)
                if (designer == null) return null;
                object tools = _toolsProp.GetValue(designer);             // .Tools
                object jwt   = tools==null ? null : _jwingToolProp.GetValue(tools);  // .JWingTool
                if (jwt == null) return null;
                object wing  = _tCurWing.GetValue(jwt);                   // .CurrentWing
                if (wing == null) return null;
                airfoil = _tSliceAirfoil?.GetValue(jwt) as string;        // .SliceAirfoil
                return wing;
            }
            catch { return null; }
        }

        private object GetLargestDesignerWing(out string airfoil)
        {
            airfoil = null;
            object best = null; float bestArea = 0f;
            try
            {
                if (_wsType==null || _wsDataProp==null) return null;
                foreach (var mb in Object.FindObjectsByType<MonoBehaviour>(FindObjectsSortMode.None))
                {
                    if (mb?.GetType()!=_wsType) continue;
                    object data=null; try { data=_wsDataProp.GetValue(mb); } catch {}
                    if (data==null) continue;
                    float area = PF(data, _jdArea);
                    if (area > bestArea) { bestArea=area; best=data; }
                }
                if (best!=null) airfoil = FirstSliceAirfoil(best);
            }
            catch {}
            return best;
        }
        private string FirstSliceAirfoil(object wing)
        {
            try { var s = _jdSlices?.GetValue(wing) as System.Collections.IList; if (s!=null && s.Count>0) return _iwsAirfoil?.GetValue(s[0]) as string; } catch {}
            return null;
        }
        private static float SaneScale(float v) => (v > 0.01f && v < 100f) ? v : 1f;

        // Craft total loaded mass from the designer in SP2's displayed/stat scale; 0 if unavailable.
        private float GetCraftMass()
        {
            try {
                object d=_designerInstanceProp?.GetValue(null); if(d==null) return 0f;
                object ac=_dAircraftProp?.GetValue(d); if(ac==null) return 0f;
                var at=ac.GetType();
                var statsType=at.GetNestedType("AircraftStats", BindingFlags.Public|BindingFlags.NonPublic);
                var getStats=at.GetMethod("GetStats", BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance);
                if(statsType!=null && getStats!=null)
                {
                    object loadedWeight=System.Enum.Parse(statsType, "LoadedWeight");
                    float mass=System.Convert.ToSingle(getStats.Invoke(ac, new object[]{loadedWeight}));
                    if(mass>0.5f && !float.IsNaN(mass) && !float.IsInfinity(mass)) return mass;
                }
                object com=_acComProp?.GetValue(ac); if(com==null) return 0f;
                float raw=System.Convert.ToSingle(_comLoadedMassProp?.GetValue(com) ?? 0f);
                return raw>0.5f && !float.IsNaN(raw) && !float.IsInfinity(raw) ? raw/0.01f : 0f;
            } catch { return 0f; }
        }
        // The mass used for weight/takeoff overlays (craft mass unless the user has overridden it).
        private float EffMassKg => _testMassKg > 0.5f ? _testMassKg : GetCraftMass();


        // Find a slider/stat value by its label text, return parsed number (strips units)
        private float FindUIValue(string label)
        {
            foreach (var tmp in Object.FindObjectsByType<TextMeshProUGUI>(FindObjectsSortMode.None))
            {
                if (tmp == null || tmp.text != label) continue;
                // The value is a sibling/nearby TMP within the same row (parent or grandparent)
                var row = tmp.transform.parent;
                for (int up = 0; up < 2 && row != null; up++, row = row.parent)
                {
                    foreach (var sib in row.GetComponentsInChildren<TextMeshProUGUI>(true))
                    {
                        if (sib == tmp) continue;
                        float v = ParseNum(sib.text);
                        if (!float.IsNaN(v)) return v;
                    }
                }
            }
            return 0f;
        }

        private static float ParseNum(string s)
        {
            if (string.IsNullOrEmpty(s)) return float.NaN;
            var sb = new System.Text.StringBuilder();
            bool dot=false, any=false;
            foreach (char c in s)
            {
                if (char.IsDigit(c)) { sb.Append(c); any=true; }
                else if (c=='.' && !dot) { sb.Append(c); dot=true; }
                else if (c=='-' && sb.Length==0) sb.Append(c);
                else if (any) break; // stop at unit after number
            }
            if (!any) return float.NaN;
            return float.TryParse(sb.ToString(), out var r) ? r : float.NaN;
        }


        // ═══════════════════════════════════════════════════════════════════════
        //  EXACT SP2 AIRFOIL MODEL — transcribed verbatim from Game.dll
        //  (NACAFoils.NACA4Digit + StandardPhysicsFunctions + SlicePolar/StallCurve/
        //  DragCurve). Reproduces SP2's per-slice 2-D polar so the designer graphs
        //  match the sim exactly. SP2 quirks (e.g. camber-position clamp) are preserved.
        // ═══════════════════════════════════════════════════════════════════════
        private const float SOS_SL = 340.29f;          // speed of sound, sea level (Constants.SpeedOfSoundAtSeaLevel)
        private const float INV_KIN_VISC_SL = 67577f;  // density/dynamicViscosity, 15°C sea level (Atmosphere)

        private struct StdParams { public float leadingEdgeRadius, deltaYParameter, maxThickness, maxThicknessLocation, meanThickness, trailingGradient, uncorrectedMaxLift, uncorrectedMinLift, aerodynamicCentre; }
        private struct Polar { public float alphaZero, liftGradient, stalledNormalForceMax, aerodynamicCentre, stallPosMax, stallPosSmooth, stallNegMax, stallNegSmooth, dragZeroLift, dragViscous; }

        private static readonly Vector3[] DesignParams = {
            new Vector3(0.9f,2.81f,-0.118f), new Vector3(0.8f,1.8f,-0.184f), new Vector3(0.76f,0.74f,-0.157f),
            new Vector3(0.75f,0f,-0.187f), new Vector3(0.76f,-0.74f,-0.222f), new Vector3(0.8f,-1.6f,-0.266f) };
        private static readonly Vector3[] ClMaxD1 = {
            new Vector3(0f,0f,0f),new Vector3(0.017f,0.055f,0.055f),new Vector3(0.128f,0.196f,0.196f),new Vector3(0.232f,0.32f,0.32f),new Vector3(0.329f,0.401f,0.401f),new Vector3(0.224f,0.306f,0.306f),new Vector3(0.115f,0.164f,0.164f),new Vector3(0.07f,0.106f,0.106f),new Vector3(0.072f,0.103f,0.103f),new Vector3(0.06f,0.098f,0.098f),new Vector3(0.031f,0.057f,0.057f),new Vector3(0f,0f,0f),
            new Vector3(0f,0f,0f),new Vector3(0f,0f,0f),new Vector3(0f,0f,0.498f),new Vector3(0.145f,0.34f,0.669f),new Vector3(0.291f,0.461f,0.604f),new Vector3(0.314f,0.396f,0.44f),new Vector3(0.22f,0.251f,0.286f),new Vector3(0.156f,0.17f,0.185f),new Vector3(0.149f,0.149f,0.149f),new Vector3(0.145f,0.164f,0.175f),new Vector3(0.094f,0.101f,0.116f),new Vector3(0f,0f,0f),
            new Vector3(0f,0f,0f),new Vector3(0f,0f,0f),new Vector3(0f,0f,0.23f),new Vector3(0f,0.229f,0.428f),new Vector3(0.168f,0.325f,0.448f),new Vector3(0.206f,0.31f,0.376f),new Vector3(0.133f,0.176f,0.242f),new Vector3(0.058f,0.088f,0.121f),new Vector3(0.0515f,0.074f,0.095f),new Vector3(0.053f,0.077f,0.11f),new Vector3(0.03f,0.051f,0.092f),new Vector3(0f,0f,0f),
            new Vector3(0f,0f,0f),new Vector3(0f,0f,0f),new Vector3(0f,0f,0.187f),new Vector3(0.031f,0.18f,0.36f),new Vector3(0.096f,0.274f,0.448f),new Vector3(0.153f,0.303f,0.447f),new Vector3(0.139f,0.215f,0.288f),new Vector3(0.088f,0.146f,0.21f),new Vector3(0.06f,0.135f,0.199f),new Vector3(0.095f,0.17f,0.24f),new Vector3(0.078f,0.121f,0.206f),new Vector3(0f,0.055f,0.098f) };
        private static readonly Vector3[] ClMaxD2 = {
            new Vector3(0.148f,0.148f,0.148f),new Vector3(0.171f,0.171f,0.171f),new Vector3(0.194f,0.194f,0.194f),new Vector3(0.205f,0.205f,0.205f),new Vector3(0.182f,0.182f,0.182f),new Vector3(0.119f,0.119f,0.119f),new Vector3(0.054f,0.052f,0.076f),new Vector3(-0.014f,0.023f,0.098f),new Vector3(-0.034f,0.038f,0.124f),new Vector3(-0.045f,0.022f,0.097f) };
        private static readonly Vector3[] ClMaxD3 = {
            new Vector3(0.117f,0.003f,-0.093f),new Vector3(0.14f,-0.08f,-0.143f),new Vector3(0.067f,-0.085f,-0.146f),new Vector3(0.004f,-0.039f,-0.094f),new Vector3(0.098f,-0.035f,-0.108f),new Vector3(0.173f,-0.07f,-0.203f),new Vector3(0.195f,-0.11f,-0.244f),new Vector3(0.191f,-0.13f,-0.26f),new Vector3(0.179f,-0.154f,-0.27f) };

        // exact-model state (rebuilt from the selected wing's NACA digits each refresh)
        private bool _exactReady; private StdParams _std; private float _exCamberH,_exCamberPos,_exTc;
        private float _evalSpeedMs = 50f;
        private bool _polarValid; private float _polarSpeed; private Polar _polar;

        private static float UnLerp(float a,float b,float x)=>(x-a)/(b-a);
        private static float Lrp(float a,float b,float t)=>a+(b-a)*t;
        private static Vector3 V3Lerp(Vector3 a,Vector3 b,float t)=>new Vector3(a.x+(b.x-a.x)*t,a.y+(b.y-a.y)*t,a.z+(b.z-a.z)*t);
        private static float Frac(float x)=>x-Mathf.Floor(x);
        private static float Quad(float a,float b,float c,float x)=>a+b*x+c*x*x;

        private static float FourDigitThickness(float x,float tc)=>tc*(1.4845f*Mathf.Sqrt(x)+x*(-0.63f+x*(-1.758f+x*(1.4215f+x*-0.5075f))));
        private static float TwoDigitCamber(float x,float h,float p){ if(x<p){float n=x/p;return h*n*(2f-n);} float n2=1f-p; return h*(1f+2f*p*(x-1f)-x*x)/(n2*n2); }
        private static void SampleFoil(float x,float h,float p,float tc,out float up,out float lo){
            float th=0f,cam; if(x!=1f){ th=FourDigitThickness(x,tc); cam=TwoDigitCamber(x,h,p); } else cam=TwoDigitCamber(1.0089f,h,p);
            up=cam+th; lo=cam-th;
        }

        private static float SampleClMaxD1(float camberAmount,float camberPos,float deltaY){
            if(camberAmount==0f)return 0f;
            deltaY=Mathf.Clamp(deltaY*2f,0f,11f);
            int n=(int)Mathf.Floor(deltaY),n2=(int)Mathf.Ceil(deltaY); float n3=Frac(deltaY);
            camberPos=Mathf.Clamp(camberPos*10f,1.5f,5f);
            float n4=camberPos<3f?1.5f:Mathf.Floor(camberPos);
            int n5=12*(camberPos<3f?0:((int)Mathf.Floor(camberPos)-2));
            int n6=12*Mathf.Max((int)Mathf.Ceil(camberPos)-2,1);
            float n7=Mathf.Max(Mathf.Ceil(camberPos),3f);
            Vector3 v=V3Lerp(ClMaxD1[n5+n],ClMaxD1[n5+n2],n3);
            Vector3 v2=V3Lerp(ClMaxD1[n6+n],ClMaxD1[n6+n2],n3);
            Vector3 v3=(n7==n4)?v:V3Lerp(v,v2,UnLerp(n4,n7,camberPos));
            float[] arr={0f,v3.x,v3.y,v3.z};
            float ca=Mathf.Clamp(camberAmount*0.5f,0f,3f);
            return Lrp(arr[(int)Mathf.Floor(ca)],arr[(int)Mathf.Ceil(ca)],Frac(ca));
        }
        private static float SampleClMaxD2(float maxThicknessPos,float deltaY){
            deltaY=Mathf.Clamp(deltaY*2f,0f,9f);
            Vector3 a=V3Lerp(ClMaxD2[(int)Mathf.Floor(deltaY)],ClMaxD2[(int)Mathf.Ceil(deltaY)],Frac(deltaY));
            float[] v={0f,a.x,a.y,a.z};
            float mp=Mathf.Clamp((maxThicknessPos-0.3f)*0.2f,0f,3f);
            return Lrp(v[(int)Mathf.Floor(mp)],v[(int)Mathf.Ceil(mp)],Frac(mp));
        }
        private static float SampleClMaxD3(float chordRe,float deltaY){
            deltaY=Mathf.Clamp((deltaY-1f)*2f,0f,7.99f);
            Vector3 a=V3Lerp(ClMaxD3[(int)Mathf.Floor(deltaY)],ClMaxD3[(int)Mathf.Ceil(deltaY)],Frac(deltaY));
            float[] v={a.x,a.y,0f,a.z};   // .xyw
            float num=Mathf.Clamp(Mathf.Log10(chordRe),3f,25f);
            int n2=Mathf.Clamp((int)Mathf.Floor(num/3f)-1,0,2);
            int n3=Mathf.Clamp((int)Mathf.Ceil(num/3f)-1,1,3);
            float[] v2={3f,6f,9f,25f};
            return n2!=n3 ? Lrp(v[n2],v[n3],UnLerp(v2[n2],v2[n3],num)) : v[n2];
        }

        private static StdParams ComputeStdParams(float h,float p,float tc){
            StdParams sp=default; sp.leadingEdgeRadius=1.1019f*tc*tc;
            float num=0f,num2=0f; Vector2 val=Vector2.zero,val2=Vector2.zero;
            for(int i=0;i<=20;i++){
                float x=i*0.05f; SampleFoil(x,h,p,tc,out float up,out float lo);
                float th=up-lo, cam=(up+lo)*0.5f; num2+=th;
                if(val.y<th)val=new Vector2(x,th);
                if(val2.y<cam)val2=new Vector2(x,cam);
                if(i==18)num=th;
            }
            sp.meanThickness=num2*0.05f; sp.maxThickness=val.y; sp.maxThicknessLocation=val.x;
            SampleFoil(0.99f,h,p,tc,out float u99,out float l99);
            sp.trailingGradient=(num-(u99-l99))*5.5555553f;
            sp.aerodynamicCentre=AeroCentre(sp);
            SampleFoil(0.06f,h,p,tc,out float u06,out _); SampleFoil(0.0015f,h,p,tc,out float u015,out _);
            sp.deltaYParameter=(u06-u015)*100f;
            float n7=Mathf.Clamp(sp.deltaYParameter,0f,4.3f);
            float n8=Mathf.Max(0.8f,0.45f*n7+0.3f);
            if(n7>2f) n8=Mathf.Min(n8,0.87f+1.6f*val.x+(1.1f*val.x-0.33f)*(n7-0.35f)*(n7-0.35f));
            float d1=SampleClMaxD1(val2.y,val2.x,sp.deltaYParameter);
            float d2=SampleClMaxD2(val.x,sp.deltaYParameter);
            sp.uncorrectedMaxLift=n8+d1+d2; sp.uncorrectedMinLift=-n8+d1-d2;
            return sp;
        }
        private static float StdLiftGradient(StdParams p,float chordRe){ return 1.05f*(5.0315f*p.maxThickness+6.2788f)*StdLiftGradientCorrection(p,chordRe); }
        private static float StdLiftGradientCorrection(StdParams p,float chordRe){
            float num=Mathf.Clamp(p.trailingGradient,0.05f,0.25f);
            float num2=Mathf.Clamp(Mathf.Log10(chordRe),6f,9f);
            float num3=Mathf.Min(0.705167f+0.9527f*num2,1f);
            float num4=3.07667f-0.34f*num2; float num5=2.695f-2.5f*num4;
            return num3+num*(num4+num*num5);
        }
        private static Vector2 StdMinMax(StdParams p,float chordRe){ float d3=SampleClMaxD3(chordRe,p.deltaYParameter); return new Vector2(p.uncorrectedMinLift-d3,p.uncorrectedMaxLift+d3); }
        private static float StdSkinFriction(float chordRe,float mach){
            float num=Mathf.Clamp(Mathf.Log10(chordRe),4f,10f);
            float num2=0.00694f-Mathf.Min(mach,5f)*0.00039f-Mathf.Clamp(mach-1f,0f,5f)*0.00072f;
            float num3=0.00155f-Mathf.Min(mach,5f)*9.6E-05f-Mathf.Clamp(mach-1f,0f,3f)*0.00017f;
            float num4=mach>1f?5.6f:7f;
            float num5=Mathf.Log(num3/num2)*(mach>1f?-1.8552996f:-2.2124622f);
            return Mathf.Pow(num-5f+num4,-num5)*(num2*Mathf.Pow(num4,num5));
        }
        private static float StdZeroLiftDrag(float chordRe,float mach,StdParams p){
            float sf=StdSkinFriction(chordRe,mach); float k=p.maxThicknessLocation>=0.3f?1.2f:2f; float mt=p.meanThickness;
            return sf*(1f+k*mt+100f*(mt*mt*mt*mt))*2f;
        }
        private static float AeroCentre(StdParams p)=>AeroCentre(p.maxThickness, 2f*Mathf.Atan(p.trailingGradient)*Mathf.Rad2Deg);
        private static float AeroCentre(float tRatio,float teDeg){
            float a=Quad(26f,-0.1244f,-0.0013f,teDeg), b=Quad(28.2f,-0.121f,-5.6E-05f,teDeg);
            float t=Mathf.Pow(Mathf.Max(0f,UnLerp(0.06f,0.21f,tRatio)),0.85f);   // guard negative base (thin foils)
            return Mathf.Clamp(Lrp(a,b,t)*0.01f,0.22f,0.28f);
        }
        private static float AlphaZeroFromDesign(float lg,float designLift,float designLiftAlpha)=>designLiftAlpha-designLift/lg;

        private static Polar BuildPolarExact(float h,float p,float tc,StdParams sp,float chordRe,float mach){
            Polar po=default; float lg=StdLiftGradient(sp,chordRe);
            float num2=Mathf.Clamp(p-2f,0f,5f);   // SP2 quirk: camberPos is 0.2–0.7 ⇒ always 0 (position has no α₀ effect)
            Vector3 val=V3Lerp(DesignParams[(int)Mathf.Floor(num2)],DesignParams[(int)Mathf.Ceil(num2)],Frac(num2));
            val*=h*1.6666666f; val.y*=Mathf.Deg2Rad;
            float a0=AlphaZeroFromDesign(lg,val.x,val.y);
            Vector2 mm=StdMinMax(sp,chordRe);
            po.liftGradient=lg; po.alphaZero=a0;
            po.stallPosMax=mm.y; po.stallPosSmooth=mm.y/lg*0.2f;
            po.stallNegMax=-mm.x; po.stallNegSmooth=mm.y/lg*0.2f;
            po.stalledNormalForceMax=1.5f; po.aerodynamicCentre=sp.aerodynamicCentre;
            po.dragZeroLift=StdZeroLiftDrag(chordRe,mach,sp); po.dragViscous=0.02f;
            return po;
        }
        // StallCurve.Sample → (cl, dcl/dα) in radians
        private static void StallSample(float liftMax,float smooth,float alphaMinusA0,float lg,out float cl,out float dcl){
            float num=smooth*lg*0.5f; float num2=(liftMax-num)/lg+smooth;
            if(alphaMinusA0<num2){ cl=alphaMinusA0*lg; dcl=lg; return; }
            float num3=num2+smooth; float num4=-lg/(2f*smooth); float num5=-2f*num4*num3;
            cl=num2*(lg-(num5+num2*num4))+alphaMinusA0*(num5+num4*alphaMinusA0); dcl=num5+2f*num4*alphaMinusA0;
        }
        // SlicePolar.Sample (alpha in radians) → cl, cd
        private static void PolarSampleExact(Polar po,float alpha,float mach,out float cl,out float cd){
            alpha-=po.alphaZero; if(float.IsNaN(alpha))alpha=0f;
            float num=1f/Mathf.Sqrt(Mathf.Max(0.51f,Mathf.Abs(1f-mach*mach)));   // Prandtl-Glauert
            float sign=Mathf.Sign(alpha);
            float clx,cly;
            if(sign>=0f) StallSample(po.stallPosMax,po.stallPosSmooth,Mathf.Abs(alpha),po.liftGradient,out clx,out cly);
            else         StallSample(po.stallNegMax,po.stallNegSmooth,Mathf.Abs(alpha),po.liftGradient,out clx,out cly);
            clx*=num*sign; cly*=num;
            float n3=po.stalledNormalForceMax*num; float s=Mathf.Sin(alpha),c=Mathf.Cos(alpha);
            float n6=s*n3,n7=c*n3; float valx=n6*c; float val2x=n6*s+po.dragZeroLift;
            bool finite = !float.IsNaN(clx)&&!float.IsInfinity(clx)&&!float.IsNaN(cly)&&!float.IsInfinity(cly);
            if(!finite || Mathf.Abs(alpha)>Mathf.PI/2f || valx*sign>clx*sign){ cl=valx; cd=val2x; }
            else { cl=clx; cd=po.dragZeroLift+po.dragViscous*clx*clx; }
        }
        // Build/cache the polar for a given airspeed (Reynolds & Mach derived sea-level)
        private Polar GetExactPolar(float chord,float speedMs,out float mach){
            float v=Mathf.Max(1f,speedMs); mach=v/SOS_SL;
            if(_polarValid && Mathf.Abs(v-_polarSpeed)<0.05f) return _polar;
            float c=chord>0.05f?chord:1f; float chordRe=Mathf.Max(1000f,c*v*INV_KIN_VISC_SL);
            _polar=BuildPolarExact(_exCamberH,_exCamberPos,_exTc,_std,chordRe,mach);
            _polarSpeed=v; _polarValid=true; return _polar;
        }

        // ── Finite-wing (3-D) layer over the exact section polar ──────────────
        // Lifting-line: evaluate the SP2 section at the downwash-reduced angle (α−αi),
        // add induced drag CL²/(πARe), and apply SP2's wing-level lift/drag scales. This
        // turns the exact 2-D airfoil into realistic whole-wing CL/CD/CM for the built wing.
        private const float OSWALD_E = 0.9f;
        private void EvalDesigner(float aoaDeg, float speedMs, WingSnap w, out float cl, out float cd, out float cm)
        {
            var po = GetExactPolar(w.chord, speedMs, out float mach);
            float ar = WingAR(w);
            float piARe = Mathf.PI * ar * OSWALD_E;
            float aRad = aoaDeg * Mathf.Deg2Rad, ai = 0f, cls = 0f, cds = 0f;
            for (int it=0; it<2; it++){ PolarSampleExact(po, aRad-ai, mach, out cls, out cds); ai = cls/piARe; }
            float cdi = cls*cls/piARe;                       // induced drag
            float cd0 = po.dragZeroLift, visc = cds - cd0;   // split section drag into parasitic + lift-dependent
            cl = cls * _liftScale;
            cd = cd0*_zldScale + visc*_viscScale + cdi;
            cm = (po.aerodynamicCentre - 0.25f) * cls;        // moment about quarter-chord
        }

        // ── Airfoil model (estimate fallback — used in flight / non-NACA / simple mode) ──
        // CL(α): 3-D lift slope through zero-lift angle, smooth post-stall decay
        private float ComputeCL(float aoaDeg, WingSnap w)
        {
            if(_solReady && !_inFlight) return InterpCurve(_solAoA, _solCL, aoaDeg);   // raw SP2 solver
            if(_exactReady && !_inFlight){ EvalDesigner(aoaDeg,_evalSpeedMs,w,out float cl,out _,out _); return cl; }
            float slope = EffSlopePerDeg(w);
            float a0    = ZeroLiftDeg(w);
            float sP    = w.stallPos>0.1f ? w.stallPos : 15f;
            float sN    = w.stallNeg<-0.1f ? w.stallNeg : -12f;
            float clMaxP= slope*(sP-a0);
            float clMaxN= slope*(sN-a0);
            if (aoaDeg > sP){ float o=aoaDeg-sP; return clMaxP*Mathf.Exp(-o*o/220f); }
            if (aoaDeg < sN){ float o=sN-aoaDeg; return clMaxN*Mathf.Exp(-o*o/220f); }
            return slope*(aoaDeg-a0);
        }

        // CM(α): pitching moment coefficient about quarter-chord.
        private float ComputeCM(float aoaDeg, WingSnap w)
        {
            if(_solReady && !_inFlight) return InterpCurve(_solAoA, _solCM, aoaDeg);   // raw SP2 solver (incl. flaps)
            if(_exactReady && !_inFlight){ EvalDesigner(aoaDeg,_evalSpeedMs,w,out _,out _,out float cm); return cm; }
            return 0f;
        }

        // CD(α): parasitic + induced (finite-wing) + post-stall rise
        private float ComputeCD(float aoaDeg, WingSnap w)
        {
            if(_solReady && !_inFlight) return InterpCurve(_solAoA, _solCD, aoaDeg);   // raw SP2 solver
            if(_exactReady && !_inFlight){ EvalDesigner(aoaDeg,_evalSpeedMs,w,out _,out float cd,out _); return cd; }
            float cl = ComputeCL(aoaDeg, w);
            float ar = WingAR(w);
            float cd0 = EffCd0(w);                           // SP2's zeroLiftDrag in flight, else parasitic estimate
            float cdi = cl*cl/(Mathf.PI*ar*0.8f);            // induced
            float sP=w.stallPos>0.1f?w.stallPos:15f, sN=w.stallNeg<-0.1f?w.stallNeg:-12f;
            float extra = 0f;
            if (aoaDeg>sP) extra=0.03f*(aoaDeg-sP);
            if (aoaDeg<sN) extra=0.03f*(sN-aoaDeg);
            return cd0 + cdi + extra;
        }

        // Lift force (kN) from the representative wing at given AoA and speed (m/s)
        private float LiftKN(float aoaDeg, float vMs, WingSnap w)
        {
            float q = 0.5f * 1.225f * vMs * vMs;     // dynamic pressure (sea level)
            float area = LiftArea(w); if (area<0.05f) area=10f;
            return q * area * ComputeCL(aoaDeg, w) / 1000f;
        }

        // Independent variable is AoA (sweep -20..20) unless X==Speed (sweep speed)
        private bool SweepIsSpeed => _xAxis == XAx.Speed;

        // Given the sweep parameter value, return (x,y) for current axes
        private void Sample(float t, WingSnap w, out float x, out float y)
        {
            if (SweepIsSpeed)
            {
                float vMs = t;                       // t is speed in m/s
                float kts = vMs * 1.944f;
                x = kts;
                _evalSpeedMs = vMs;                  // Reynolds for the exact polar tracks the swept speed
                float aoa = _inFlight ? w.aoa : _testAoADeg;
                switch(_yAxis){
                    case YAx.CL:   y = ComputeCL(aoa,w); break;
                    case YAx.CD:   y = ComputeCD(aoa,w); break;
                    case YAx.LD:   { float cd=ComputeCD(aoa,w); y=cd>1e-4f?ComputeCL(aoa,w)/cd:0; break; }
                    case YAx.CM:   y = ComputeCM(aoa,w); break;
                    default:       y = LiftKN(aoa, vMs, w); break;     // Lift grows with V²
                }
            }
            else
            {
                float aoa = t;                       // t is AoA in degrees
                _evalSpeedMs = _inFlight ? _speedMs : _testSpeedMs;   // fixed airspeed for the AoA sweep
                x = _xAxis==XAx.AoA ? aoa : ComputeCD(aoa,w);
                switch(_yAxis){
                    case YAx.CL:   y = ComputeCL(aoa,w); break;
                    case YAx.CD:   y = ComputeCD(aoa,w); break;
                    case YAx.LD:   { float cd=ComputeCD(aoa,w); y=cd>1e-4f?ComputeCL(aoa,w)/cd:0; break; }
                    case YAx.CM:   y = ComputeCM(aoa,w); break;
                    default:       y = LiftKN(aoa, _inFlight?_speedMs:_testSpeedMs, w); break;
                }
            }
        }

        // ── Graph ─────────────────────────────────────────────────────────────
        private void RedrawGraph()
        {
            _graphTex.SetPixels(_graphBg);

            string subtitle = SweepIsSpeed
                ? $"{YNames[(int)_yAxis]} vs Speed  @ AoA {(_inFlight?_rep.aoa:_testAoADeg):F0}°"
                : (_yAxis==YAx.Lift ? $"{YNames[(int)_yAxis]} vs {XNames[(int)_xAxis]}  @ {(_inFlight?_speedMs*1.944f:_testSpeedMs*1.944f):F0}kts"
                                    : $"{YNames[(int)_yAxis]} vs {XNames[(int)_xAxis]}");
            if (_axisLabel) _axisLabel.text = subtitle;

            if (_wings.Count==0){ _graphTex.Apply(); ClearTicks(); return; }
            var w = _rep;

            // Data parameter range (the swept extent): AoA = solver cache span; Speed = 0..spdMax.
            float spdMax = Mathf.Max(200f, _testSpeedMs);
            float dataT0 = SweepIsSpeed ? 0f     : (_solReady && _solAoA.Count>0 ? _solAoA[0]                 : -45f);
            float dataT1 = SweepIsSpeed ? spdMax : (_solReady && _solAoA.Count>0 ? _solAoA[_solAoA.Count-1]   :  45f);
            const int N=240;

            // Auto-fit bounds come from the full data range.
            float xMinR=float.MaxValue,xMaxR=float.MinValue,yMinR=float.MaxValue,yMaxR=float.MinValue;
            for(int i=0;i<=N;i++){ float t=Mathf.Lerp(dataT0,dataT1,(float)i/N); Sample(t,w,out var x,out var y);
                if(x<xMinR)xMinR=x;if(x>xMaxR)xMaxR=x;if(y<yMinR)yMinR=y;if(y>yMaxR)yMaxR=y; }
            NiceBounds(xMinR,xMaxR,true,out float xMin,out float xMax,out float xStep);
            NiceBounds(yMinR,yMaxR,true,out float yMin,out float yMax,out float yStep);

            // Pan/zoom: keep the view synced to auto until the user grabs it, then honour their window.
            if(!_viewCustom){ _vxMin=xMin;_vxMax=xMax;_vyMin=yMin;_vyMax=yMax; }
            else { xMin=_vxMin;xMax=_vxMax;yMin=_vyMin;yMax=_vyMax;
                   xStep=NiceStep(Mathf.Max(1e-6f,(xMax-xMin)/4f)); yStep=NiceStep(Mathf.Max(1e-6f,(yMax-yMin)/4f)); }

            DrawAlignedGrid(xMin,xMax,xStep,yMin,yMax,yStep);

            int zx=MapX(0,xMin,xMax), zy=MapY(0,yMin,yMax);
            if(zx>=0&&zx<GW)for(int y=0;y<GH;y++)_graphTex.SetPixel(zx,y,new Color(0.3f,0.32f,0.4f));
            if(zy>=0&&zy<GH)for(int x=0;x<GW;x++)_graphTex.SetPixel(x,zy,new Color(0.3f,0.32f,0.4f));

            // Draw across the VISIBLE x-range so the curve extends when you zoom/pan out.
            // (Map the view's x back to the sweep parameter t: AoA → degrees; Speed → kts/1.944.)
            float drawT0, drawT1;
            if (SweepIsSpeed)            { drawT0=Mathf.Max(0f, xMin/1.944f);        drawT1=Mathf.Max(drawT0+0.1f, xMax/1.944f); }
            else if (_xAxis==XAx.AoA)    { drawT0=Mathf.Clamp(xMin,-90f,89f);        drawT1=Mathf.Clamp(xMax,drawT0+0.1f,90f); }
            else                         { drawT0=dataT0;                            drawT1=dataT1; }   // X=CD is parametric

            int px0=-1,py0=-1;
            var curveCol=new Color(0.35f,0.80f,1f);
            for(int i=0;i<=N;i++){ float t=Mathf.Lerp(drawT0,drawT1,(float)i/N); Sample(t,w,out var x,out var y);
                int px=MapX(x,xMin,xMax), py=MapY(y,yMin,yMax);
                if(px0>=0){ Line(_graphTex,GW,GH,px0,py0,px,py,curveCol); Line(_graphTex,GW,GH,px0,py0+1,px,py+1,curveCol); }
                px0=px;py0=py;
            }

            // Weight line: on Lift plots, a horizontal line at W = m·g (kN). Where the lift curve
            // crosses it the wing is supporting the craft → lift-off / 1-g flight at that x.
            if(_yAxis==YAx.Lift){ float wkN=EffMassKg*9.81f/1000f; if(wkN>0f){ int wy=MapY(wkN,yMin,yMax);
                if(wy>=0&&wy<GH){ var wc=new Color(1f,0.55f,0.2f); for(int x=0;x<GW;x+=6){ _graphTex.SetPixel(x,wy,wc); if(x+1<GW)_graphTex.SetPixel(x+1,wy,wc); } } } }

            if(_inFlight){ Sample(SweepIsSpeed?_speedMs:w.aoa, w, out var cxv, out var cyv);
                Cross(_graphTex,GW,GH,MapX(cxv,xMin,xMax),MapY(cyv,yMin,yMax),Color.yellow); }

            _graphTex.Apply();

            // On-graph tick numbers at every gridline step (magnitude-aware, clean)
            string xu=SweepIsSpeed?"kts":XUnits[(int)_xAxis];
            string yu=YUnits[(int)_yAxis];
            DrawTicks(xMin,xMax,xStep,xu, yMin,yMax,yStep,yu);
            if(_scaleLabel) _scaleLabel.text = SweepIsSpeed
                ? $"Speed 0–{Mathf.Max(200f,_testSpeedMs)*1.944f:F0} kts · AoA {(_inFlight?w.aoa:_testAoADeg):F0}° · wing S {w.area:F1}m²"
                : (_yAxis==YAx.Lift?$"AoA −35…+35° · {(_inFlight?_speedMs*1.944f:_testSpeedMs*1.944f):F0}kts · wing S {w.area:F1}m²":"AoA −35…+35°");
        }

        private void ClearTicks()
        {
            foreach(var t in _xTickPool) if(t)t.gameObject.SetActive(false);
            foreach(var t in _yTickPool) if(t)t.gameObject.SetActive(false);
        }

        // Faint gridlines at each labelled step (zero axes are drawn brighter afterwards)
        private void DrawAlignedGrid(float xMin,float xMax,float xStep, float yMin,float yMax,float yStep)
        {
            var d=new Color(0.14f,0.16f,0.2f);
            if(xStep>1e-6f) for(float v=Mathf.Ceil(xMin/xStep)*xStep; v<=xMax+xStep*0.5f; v+=xStep){ int px=MapX(v,xMin,xMax); for(int y=0;y<GH;y++)_graphTex.SetPixel(px,y,d); }
            if(yStep>1e-6f) for(float v=Mathf.Ceil(yMin/yStep)*yStep; v<=yMax+yStep*0.5f; v+=yStep){ int py=MapY(v,yMin,yMax); for(int x=0;x<GW;x++)_graphTex.SetPixel(x,py,d); }
        }

        // Place a pooled tick label at one gridline step (units shown only on the axis extreme)
        private void DrawTicks(float xMin,float xMax,float xStep,string xu, float yMin,float yMax,float yStep,string yu)
        {
            int yi=0;
            if(yStep>1e-6f) for(float v=Mathf.Ceil(yMin/yStep)*yStep; v<=yMax+yStep*0.5f; v+=yStep, yi++){
                int py=MapY(v,yMin,yMax);
                var t=PoolTick(_yTickPool,yi,ML-3,TextAlignmentOptions.Right);
                ((RectTransform)t.transform).anchoredPosition=new Vector2(0,-(GH-py)-7);
                t.text=Fmt(v, Mathf.Abs(v-yMax)<yStep*0.5f?yu:"");
            }
            for(int k=yi;k<_yTickPool.Count;k++) _yTickPool[k].gameObject.SetActive(false);

            int xi=0;
            if(xStep>1e-6f) for(float v=Mathf.Ceil(xMin/xStep)*xStep; v<=xMax+xStep*0.5f; v+=xStep, xi++){
                int px=MapX(v,xMin,xMax);
                // Left-align the first label so it can't reach left into the Y-axis labels; centre the rest.
                bool firstLeft = (xi==0 && px < 24);
                var t=PoolTick(_xTickPool,xi,46, firstLeft?TextAlignmentOptions.Left:TextAlignmentOptions.Center);
                float lx = firstLeft ? ML+px : Mathf.Clamp(ML+px-23, 0f, GW+ML-46);
                ((RectTransform)t.transform).anchoredPosition=new Vector2(lx,-GH-2);
                t.text=Fmt(v, Mathf.Abs(v-xMax)<xStep*0.5f?xu:"");
            }
            for(int k=xi;k<_xTickPool.Count;k++) _xTickPool[k].gameObject.SetActive(false);
        }

        private TextMeshProUGUI PoolTick(List<TextMeshProUGUI> pool, int i, float width, TextAlignmentOptions align)
        {
            while(pool.Count<=i) pool.Add(MakeTick(_gcT, Vector2.zero, width, align));
            var t=pool[i]; t.gameObject.SetActive(true); t.alignment=align;
            ((RectTransform)t.transform).sizeDelta=new Vector2(width,14);
            return t;
        }

        // Magnitude-aware number formatting for ticks
        private static string Fmt(float v, string unit)
        {
            float a=Mathf.Abs(v);
            string s = a>=100 ? v.ToString("F0") : a>=10 ? v.ToString("F0") : a>=1 ? v.ToString("F1") : v.ToString("F2");
            return string.IsNullOrEmpty(unit) ? s : s+unit;
        }

        // Texture y=0 is the BOTTOM row, so max value → high row (top). (Previously inverted.)
        private int MapX(float v,float lo,float hi)=>Mathf.Clamp((int)((v-lo)/(hi-lo)*(GW-1)),0,GW-1);
        private int MapY(float v,float lo,float hi)=>Mathf.Clamp((int)((v-lo)/(hi-lo)*(GH-1)),0,GH-1);

        // Round axis bounds to clean numbers (1/2/5 ×10ⁿ) for readable ticks
        private static void NiceBounds(float mn, float mx, bool includeZero, out float lo, out float hi, out float step)
        {
            if(includeZero){ if(mn>0)mn=0; if(mx<0)mx=0; }
            if(mx-mn<1e-6f){ mx=mn+1; }
            step=NiceStep((mx-mn)/4f);
            lo=Mathf.Floor(mn/step)*step;
            hi=Mathf.Ceil(mx/step)*step;
            if(hi-lo<1e-6f)hi=lo+step;
        }
        private static float NiceStep(float raw)
        {
            if(raw<=0)return 1;
            float mag=Mathf.Pow(10,Mathf.Floor(Mathf.Log10(raw)));
            float n=raw/mag;
            float nice = n<1.5f?1f : n<3f?2f : n<7f?5f : 10f;
            return nice*mag;
        }

        private void UpdateStats()
        {
            if(_statsText==null)return;
            if(_wings.Count==0){_statsText.text=_inFlight?"No wing physics.":"<color=#8899ff>Designer — geometry loading…</color>";return;}

            var rep = _rep;
            float ar    = WingAR(rep);
            float slope, slope3d, a0, clMax, stP, stN; string src, foil;
            bool solverMode = _solReady && !_inFlight;
            if (solverMode)
            {
                // Everything derived straight from the cached raw-solver CL/CD curve.
                slope3d = slope = (InterpCurve(_solAoA,_solCL,2f) - InterpCurve(_solAoA,_solCL,-2f)) / 4f;  // per-deg near 0
                a0 = CurveZeroCross(_solAoA, _solCL);
                clMax = -1e9f; stP = 0f; float clMin=1e9f; stN=0f;
                for (int i=0;i<_solCL.Count;i++){ if(_solCL[i]>clMax){clMax=_solCL[i];stP=_solAoA[i];} if(_solCL[i]<clMin){clMin=_solCL[i];stN=_solAoA[i];} }
                src = "<color=#44ddaa>SP2 solver</color>";
                foil = string.IsNullOrEmpty(_airfoilName) ? "" : $" <color=#cfe6ff>{_airfoilName}</color>";
            }
            else if (_exactReady && !_inFlight)
            {
                _evalSpeedMs = _testSpeedMs;
                var po = GetExactPolar(rep.chord, _evalSpeedMs, out _);
                float a2d = po.liftGradient;                                   // per-rad, 2-D section
                float a3d = a2d/(1f + a2d/(Mathf.PI*ar*OSWALD_E));             // finite-wing slope
                slope = a2d * Mathf.Deg2Rad; slope3d = a3d * Mathf.Deg2Rad;    // per-deg
                a0    = po.alphaZero * Mathf.Rad2Deg;
                clMax = po.stallPosMax * _liftScale;
                float trP=(po.stallPosMax-po.stallPosSmooth*a2d*0.5f)/a2d+po.stallPosSmooth;
                float trN=(po.stallNegMax-po.stallNegSmooth*a2d*0.5f)/a2d+po.stallNegSmooth;
                stP = (po.alphaZero+trP)*Mathf.Rad2Deg; stN = (po.alphaZero-trN)*Mathf.Rad2Deg;
                src = "<color=#44ddaa>SP2 exact + 3-D</color>";
                foil = string.IsNullOrEmpty(_airfoilName) ? "" : $" <color=#cfe6ff>{_airfoilName}</color>";
            }
            else
            {
                slope = slope3d = EffSlopePerDeg(rep); a0 = ZeroLiftDeg(rep); clMax = ComputeCL(rep.stallPos, rep);
                stP = rep.stallPos; stN = rep.stallNeg;
                src = rep.fromSp2Model ? "<color=#44ddaa>SP2 model</color>" : "<color=#cc9944>estimate</color>";
                foil = "";
            }
            float ldMax = BestLD(rep, out float ldAoA);

            float liftArea = LiftArea(rep);
            var sb=new System.Text.StringBuilder();
            sb.AppendLine($"<color=#aaddff><b>Main wing</b></color>{foil}   Area {rep.area:F1} m²   Span {rep.span:F1} m   <b>AR {ar:F1}</b>");
            if (_exactReady && !_inFlight && !solverMode)
                sb.AppendLine($"<color=#aaddff><b>Lift</b></color>  slope <b>{slope3d:F3}</b>/° wing ({slope:F3} 2-D)   zero-lift α {a0:F1}°   t/c {rep.thickness:F2}");
            else
                sb.AppendLine($"<color=#aaddff><b>Lift</b></color>  slope <b>{slope3d:F3}</b>/° (per deg)   zero-lift α {a0:F1}°   t/c {rep.thickness:F2}");
            sb.AppendLine($"<color=#aaddff><b>Stall</b></color>  CLmax {clMax:F2}   +{stP:F0}° / {stN:F0}°   <color=#44ddaa>best L/D {ldMax:F1} @ {ldAoA:F0}°</color>   [{src}]");
            float massKg = EffMassKg;
            if (massKg>0.5f && clMax>0.05f && liftArea>0.05f)
            {
                float vStall = Mathf.Sqrt(2f*massKg*9.81f/(1.225f*liftArea*clMax));   // m/s at CLmax over the scoped area
                string scopeTag = _aeroScopeAll ? "all lifting" : "wing+mirror";
                sb.AppendLine($"<color=#aaddff><b>Takeoff</b></color>  {massKg:F0} kg   <color=#ffaa55>Vstall {vStall*1.944f:F0} kts</color>   rotate ~{vStall*1.1f*1.944f:F0} kts   <color=#888fa8>({liftArea:F0} m² {scopeTag})</color>");
            }
            if (_totalAreaAll > 0.5f)
                sb.AppendLine($"<color=#888fa8>All surfaces {_totalAreaAll:F0} m² · {_wings.Count} pieces</color>");
            if(_inFlight)
                sb.AppendLine($"<color=#44ff88>Lift</color> {_totalLiftN/1000f:F1} kN   <color=#ff8844>Drag</color> {_totalDragN/1000f:F2} kN   <color=#88aaff>AoA</color> {rep.aoa:F1}°   <color=#88aaff>Spd</color> {_speedMs*1.944f:F0} kts");
            _statsText.text=sb.ToString();
            // Size the layout slot to the text's true rendered height (handles wrapped lines)
            // so the ScrollRect content extends far enough — otherwise the bottom rows clip
            // and the view snaps back when you try to scroll to them.
            if(_statsLE!=null){ _statsText.ForceMeshUpdate(); _statsLE.preferredHeight = Mathf.Ceil(_statsText.preferredHeight) + 6f; }
        }

        private float BestLD(WingSnap w, out float atAoA)
        {
            float best=0; atAoA=0;
            // Sweep below 0° too — a cambered wing's best L/D can sit at small/negative AoA
            for(float a=-5f;a<=18f;a+=0.25f){ float cd=ComputeCD(a,w); if(cd<1e-4f)continue; float ld=ComputeCL(a,w)/cd; if(ld>best){best=ld;atAoA=a;} }
            return best;
        }

        // ── UI factories ──────────────────────────────────────────────────────
        // A button built from scratch (Image + Button + stretched TMP label). Cloning SP2's native button
        // proved unreliable for some rows (blank label / dead clicks under the Unity 6 update), so these
        // control buttons are self-contained and deterministic.
        private GameObject MakeTextButton(Transform parent, string label, System.Action onClick)
        {
            var go=new GameObject(label+"Btn"); go.transform.SetParent(parent,false);
            var img=go.AddComponent<UnityEngine.UI.Image>(); img.color=new Color(0.20f,0.36f,0.58f,1f); img.raycastTarget=true;
            var btn=go.AddComponent<Button>(); btn.targetGraphic=img; btn.interactable=true;
            var le=go.AddComponent<LayoutElement>(); le.preferredHeight=28; le.preferredWidth=120; le.flexibleWidth=1;
            var txt=MakeText(label, go.transform, _fontSize*0.8f);
            txt.alignment=TextAlignmentOptions.Center; txt.raycastTarget=false;
            var trt=txt.rectTransform; trt.anchorMin=Vector2.zero; trt.anchorMax=Vector2.one; trt.offsetMin=Vector2.zero; trt.offsetMax=Vector2.zero;
            btn.onClick.AddListener(()=>{ PlayClick(); onClick(); });
            return go;
        }

        private TextMeshProUGUI MakeText(string txt, Transform parent, float size)
        {
            var go=new GameObject("Txt"); go.transform.SetParent(parent,false);
            var t=go.AddComponent<TextMeshProUGUI>();
            t.text=txt; t.fontSize=size; t.color=_fontColor;
            if(_font!=null)t.font=_font;
            if(_fontMat!=null)t.fontSharedMaterial=_fontMat;
            t.richText=true;
            return t;
        }

        private GameObject MakeRow(string name, Transform parent, float h)
        {
            var go=new GameObject(name); go.transform.SetParent(parent,false);
            var hlg=go.AddComponent<HorizontalLayoutGroup>();
            hlg.childForceExpandWidth=true; hlg.childForceExpandHeight=true;
            hlg.spacing=6; hlg.childAlignment=TextAnchor.MiddleCenter;
            var le=go.AddComponent<LayoutElement>(); le.preferredHeight=h; le.flexibleWidth=1;
            return go;
        }

        // ── Utility ───────────────────────────────────────────────────────────
        private static GameObject FindGO(string n){var g=GameObject.Find(n);if(g!=null)return g;foreach(var go in Object.FindObjectsByType<GameObject>(FindObjectsSortMode.None))if(go!=null&&go.name==n)return go;return null;}
        private static T FindInChildren<T>(Transform r)where T:Component{var q=new Queue<Transform>();q.Enqueue(r);while(q.Count>0){var t=q.Dequeue();var c=t.GetComponent<T>();if(c!=null)return c;for(int i=0;i<t.childCount;i++)q.Enqueue(t.GetChild(i));}return null;}
        private static float TCL(float a,float lg,float a0,float sP,float sN,float cM,float cm){float att=lg*Mathf.Deg2Rad*(a-a0);if(a>sP)return Mathf.Lerp(cM,cM*0.55f,Mathf.Clamp01((a-sP)/12f));if(a<sN)return Mathf.Lerp(cm,cm*0.55f,Mathf.Clamp01((sN-a)/12f));return att;}
        private static void Cross(Texture2D t,int w,int h,int x,int y,Color c){for(int d=-6;d<=6;d++){int qx=x+d,qy=y+d;if(qx>=0&&qx<w)t.SetPixel(qx,y,c);if(qy>=0&&qy<h)t.SetPixel(x,qy,c);}}
        private static int VY(int h,float v,float sc){if(sc<0.0001f)return h/2;return Mathf.Clamp((int)(h/2-v/sc*(h/2-8)),0,h-1);}
        private static void Grid(Texture2D t,int w,int h){var z=new Color(0.3f,0.33f,0.4f);var d=new Color(0.14f,0.16f,0.2f);for(int x=0;x<w;x++){t.SetPixel(x,h/2,z);t.SetPixel(x,h/4,d);t.SetPixel(x,3*h/4,d);}for(int y=0;y<h;y++){t.SetPixel(w/4,y,d);t.SetPixel(w/2,y,d);t.SetPixel(3*w/4,y,d);}}
        private static void Line(Texture2D t,int tw,int th,int x0,int y0,int x1,int y1,Color c){int dx=Mathf.Abs(x1-x0),dy=Mathf.Abs(y1-y0),sx=x0<x1?1:-1,sy=y0<y1?1:-1,e=dx-dy;while(true){if(x0>=0&&x0<tw&&y0>=0&&y0<th)t.SetPixel(x0,y0,c);if(x0==x1&&y0==y1)break;int e2=2*e;if(e2>-dy){e-=dy;x0+=sx;}if(e2<dx){e+=dx;y0+=sy;}}}
        private static Color[] Fill(int n,Color c){var a=new Color[n];for(int i=0;i<n;i++)a[i]=c;return a;}
        private static float ISA(float h)=>1.225f*Mathf.Pow(Mathf.Max(0f,1f-0.0000226f*h),4.256f);
        private static float PF(object o,PropertyInfo p){if(p==null)return 0f;try{return System.Convert.ToSingle(p.GetValue(o));}catch{return 0f;}}
        private static float FF(object o,FieldInfo f){if(f==null||o==null)return 0f;try{return System.Convert.ToSingle(f.GetValue(o));}catch{return 0f;}}
    }
}
