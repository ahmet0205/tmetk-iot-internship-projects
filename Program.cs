using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using StereoKit;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Protocol;

// --- TYPES ---
class CarQ
{
    public Model[] modelSet = null!;
    public int modelIdx;
    public float t;
    public bool stopped;
}

record VehEvent(
    int bodyNo,
    string katashiki,
    string colorExtCode,
    string vinNo,
    string carFamily,
    string loDate
);

class Program
{
    // === MQTT CONFIG ===
    const string MQTT_HOST = "10.116.116.20";
    const int MQTT_PORT = 7000;           // broker'in gerçek portu neyse onu yaz
    const string MQTT_CARS_TOPIC = "IOT252/#";

    // === GLOBAL STATE ===
    static readonly ConcurrentQueue<VehEvent> mqttQueue = new();
    static List<CarQ> cars = new();
    static DateTime lastSpeedAtUtc = DateTime.MinValue;
    static Sound bg;
    static SoundInst bgInst;

    // === Renk kodu -> Color ===
    static readonly Dictionary<string, Color> ColorMap =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["785"] = new Color(0.078f, 0.255f, 0.439f), // pasifik mavi
            ["2YB"] = new Color(0.078f, 0.255f, 0.439f), // pasifik mavi chr

            ["1G3"] = new Color(0.431f, 0.431f, 0.431f), // metalik gri
            ["2NB"] = new Color(0.431f, 0.431f, 0.431f), // metalik gri chr

            ["1K6"] = new Color(0.333f, 0.341f, 0.325f), // duman gri

            ["3U5"] = new Color(0.608f, 0.106f, 0.188f), // egzotik kırmızı
            ["2TB"] = new Color(0.608f, 0.106f, 0.188f), // egzotik kırmızı chr
            ["M35"] = new Color(1.000f, 0.271f, 0.000f), // magma kırmızı chr

            ["209"] = new Color(0.0f, 0.0f, 0.0f),       // siyah
            ["040"] = new Color(1.0f, 1.0f, 1.0f),       // kar beyazı

            ["089"] = new Color(0.957f, 0.973f, 0.976f), // galaksi beyaz
            ["2VU"] = new Color(0.600f, 0.600f, 0.600f), // parlak gümüş gri

            ["1J6"] = new Color(0.753f, 0.753f, 0.753f), // kristal gri
            ["2MR"] = new Color(0.753f, 0.753f, 0.753f), // kristal gri chr
            ["1L0"] = new Color(0.600f, 0.600f, 0.600f), // parlak gümüş gri chr1
        };

    // === MATERIAL ID/LABEL EŞLEŞMELERİ ===
    static readonly HashSet<string> CorollaPaintMatIds =
        new(StringComparer.OrdinalIgnoreCase) { "corollaquw_paint.006" };
    static readonly string[] CorollaContains = { "corolla", "paint" };

    static readonly HashSet<string> ChrBodyMatIds =
        new(StringComparer.OrdinalIgnoreCase) { "chr_body_paint" };
    static readonly HashSet<string> ChrRoofMatIds =
        new(StringComparer.OrdinalIgnoreCase) { "chr_roof_paint" };
    static readonly HashSet<string> ChrSecondaryMatIds =
        new(StringComparer.OrdinalIgnoreCase) { "chr_secondary_paint" };

    static readonly string[] ChrBodyContains = { "chr", "govde", "paint" };
    static readonly string[] ChrRoofContains = { "chr", "cati", "paint" };
    static readonly string[] ChrSecContains = { "chr", "ikincil", "paint" };

    // BASE SETS
    static Model[] baseCorolla = null!;
    static Model[] baseChr = null!;

    static volatile int latestBeltSpeed = 1;
    static volatile bool beltStop = false;

    // ---------- HELPERS ----------
    static bool IdMatches(string? id, HashSet<string> exact, string[] containsAny)
    {
        if (string.IsNullOrEmpty(id)) return false;
        if (exact.Contains(id)) return true;
        string low = id.ToLowerInvariant();
        int hit = 0;
        foreach (var kw in containsAny) if (low.Contains(kw)) hit++;
        return hit >= 2;
    }

    static void TintModel(Model m, Func<string?, bool> pick, Color col)
    {
        foreach (var v in m.Visuals)
        {
            var mat = v.Material;
            if (mat == null) continue;
            if (pick(mat.Id))
            {
                var nm = mat.Copy();
                nm[MatParamName.ColorTint] = col;
                v.Material = nm;
            }
        }
    }

    static Model[] MakeCorollaSetColored(Color bodyCol, Model[] baseSet)
    {
        var outSet = new Model[baseSet.Length];
        for (int i = 0; i < baseSet.Length; i++)
        {
            var copy = baseSet[i].Copy();
            TintModel(copy, id => IdMatches(id, CorollaPaintMatIds, CorollaContains), bodyCol);
            outSet[i] = copy;
        }
        return outSet;
    }

    static Model[] MakeChrSetColored(Color bodyCol, Color roofCol, Color secCol, Model[] baseSet)
    {
        var outSet = new Model[baseSet.Length];
        for (int i = 0; i < baseSet.Length; i++)
        {
            var copy = baseSet[i].Copy();
            TintModel(copy, id => IdMatches(id, ChrBodyMatIds, ChrBodyContains), bodyCol);
            TintModel(copy, id => IdMatches(id, ChrRoofMatIds, ChrRoofContains), roofCol);
            TintModel(copy, id => IdMatches(id, ChrSecondaryMatIds, ChrSecContains), secCol);
            outSet[i] = copy;
        }
        return outSet;
    }

    static bool TryGetPropertyCI(JsonElement obj, string name, out JsonElement value)
    {
        foreach (var p in obj.EnumerateObject())
            if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = p.Value; return true;
            }
        value = default;
        return false;
    }

    static string? GetStrCI(JsonElement obj, string name)
    {
        if (!TryGetPropertyCI(obj, name, out var el))
            return null;
        return el.ValueKind switch
        {
            JsonValueKind.String => el.GetString(),
            JsonValueKind.Number => el.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => el.ToString()
        };
    }

    // carFamily datasında kod geliyor (örn. "x94W" = Corolla, "x00W" = C-HR).
    static string DecideModelId(string? carFamily, string? katashiki)
    {
        var cf = (carFamily ?? "").Trim().ToLowerInvariant();
        if (cf == "x00w" || cf.Contains("chr")) return "x00W"; // CHR
        return "x94W";                                         // Corolla (default)
    }

    // ---- ROBUST JSON PARSER (trim, boşluk, case, string-sayı toleransı) ----
    static bool TryParseVehEvent(string raw, out VehEvent veh, out string err)
    {
        veh = default!;
        err = "";
        try
        {
            if (string.IsNullOrWhiteSpace(raw)) { err = "empty payload"; return false; }

            // UTF8 + kirler
            raw = raw.Trim();
            if (raw.StartsWith("'") && raw.EndsWith("'")) raw = raw.Trim('\''); // tek tırnak gelirse
            int firstBrace = raw.IndexOf('{');
            int lastBrace = raw.LastIndexOf('}');
            if (firstBrace >= 0 && lastBrace > firstBrace)
            {
                raw = raw.Substring(firstBrace, lastBrace - firstBrace + 1);
            }

            using var doc = JsonDocument.Parse(raw);
            var r = doc.RootElement;

            // bodyNo: number veya numeric-string destekle
            int bodyNo;
            if (TryGetPropertyCI(r, "bodyNo", out var bodyNoEl))
            {
                if (bodyNoEl.ValueKind == JsonValueKind.Number) bodyNo = bodyNoEl.GetInt32();
                else if (bodyNoEl.ValueKind == JsonValueKind.String &&
                         int.TryParse(bodyNoEl.GetString()?.Trim(), out var bn)) bodyNo = bn;
                else { err = "bodyNo invalid"; return false; }
            }
            else { err = "bodyNo missing"; return false; }

            string katashiki = GetStrCI(r, "katashiki")?.Trim() ?? "";
            string colorExt = GetStrCI(r, "colorExtCode")?.Trim() ?? "";
            // kodun içindeki boşlukları sil ve upper yap
            if (!string.IsNullOrEmpty(colorExt)) colorExt = Regex.Replace(colorExt, @"\s+", "").ToUpperInvariant();

            string vinNo = GetStrCI(r, "vinNo")?.Trim() ?? "";
            string carFamily = GetStrCI(r, "carFamily")?.Trim() ?? "";
            string loDate = GetStrCI(r, "loDate")?.Trim() ?? "";

            if (string.IsNullOrWhiteSpace(colorExt) || string.IsNullOrWhiteSpace(carFamily))
            {
                err = "missing required fields (colorExtCode/carFamily)";
                return false;
            }

            veh = new VehEvent(bodyNo, katashiki, colorExt, vinNo, carFamily, loDate);
            return true;
        }
        catch (Exception ex)
        {
            err = ex.Message;
            return false;
        }
    }

    static async Task StartMqttAsync(string? host = null, int? port = null, string? carsTopic = null)
    {
        string broker = string.IsNullOrWhiteSpace(host) ? MQTT_HOST : host!;
        int prt = port ?? MQTT_PORT;
        string topic = string.IsNullOrWhiteSpace(carsTopic) ? MQTT_CARS_TOPIC : carsTopic!;
        if (string.IsNullOrWhiteSpace(broker) || string.IsNullOrWhiteSpace(topic))
        {
            Log.Err("Fill MQTT_HOST / MQTT_PORT / MQTT_CARS_TOPIC"); return;
        }

        var factory = new MqttFactory();
        var client = factory.CreateMqttClient();

        client.ConnectedAsync += _ => { Log.Info("[MQTT] Connected"); return Task.CompletedTask; };
        client.DisconnectedAsync += e => { Log.Warn($"[MQTT] Disconnected: {e.Reason}"); return Task.CompletedTask; };

        client.ApplicationMessageReceivedAsync += e =>
        {
            try
            {
                string t = e.ApplicationMessage.Topic ?? "";
                // PayloadSegment.Array bazı versiyonlarda null olabilir -> Payload kullan
                var payload = e.ApplicationMessage.Payload;
                string raw = (payload is { Length: > 0 }) ? Encoding.UTF8.GetString(payload) : "";

                if (t.StartsWith(topic.Split('#')[0], StringComparison.OrdinalIgnoreCase))
                {
                    if (TryParseVehEvent(raw, out var veh, out var why))
                    {
                        mqttQueue.Enqueue(veh);
                        Log.Info($"[ENQ] bodyNo={veh.bodyNo} carFamily={veh.carFamily} kat={veh.katashiki} ext={veh.colorExtCode}");
                    }
                    else
                    {
                        Log.Err($"[SUB] cars parse fail: {why} | raw={raw}");
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Err("[SUB] parse fail: " + ex.Message);
            }
            return Task.CompletedTask;
        };

        var optsB = new MqttClientOptionsBuilder()
            .WithTcpServer(broker, prt)
            .WithClientId("sk-cars-sub-" + Guid.NewGuid())
            .WithCleanSession(true)
            .WithKeepAlivePeriod(TimeSpan.FromSeconds(30));

        await client.ConnectAsync(optsB.Build());
        await client.SubscribeAsync(new MqttClientSubscribeOptionsBuilder()
            .WithTopicFilter(f => f.WithTopic(topic).WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce))
            .Build());
    }

    // ---------- MAIN ----------
    static void Main()
    {
        var settings = new SKSettings
        {
            appName = "ManufacturingBelt",
            displayPreference = DisplayMode.Flatscreen,
            assetsFolder = "Assets"
        };
        if (!SK.Initialize(settings)) return;

        // --- Baz setleri yükle ---
        baseCorolla = new[] {
            Model.FromFile("beyazpre.glb"),
            Model.FromFile("beyazkaput.glb"),
            Model.FromFile("beyazfar.glb"),
            Model.FromFile("beyaztampon.glb"),
        };

        baseChr = new[] {
            Model.FromFile("chrPre.glb"),
            Model.FromFile("chrKaput.glb"),
            Model.FromFile("chrFar.glb"),
            Model.FromFile("chrTampon.glb"),
        };

        //background glb
        Model background = Model.FromFile("harita.glb");

        //sound
        bg = Sound.FromFile("bg.mp3");
        bgInst = bg.Play(Input.Head.position, 0.35f);




        for (int i = 0; i < baseCorolla.Length; i++)
            Log.Info($"[LOAD] baseCorolla[{i}] -> {(baseCorolla[i] != null ? "OK" : "NULL")}");
        for (int i = 0; i < baseChr.Length; i++)
            Log.Info($"[LOAD] baseChr[{i}] -> {(baseChr[i] != null ? "OK" : "NULL")}");

        // MQTT
        _ = StartMqttAsync();

        // --- Sahne ---
        float beltLen = 80f, beltW = 1.5f, beltH = 0.5f;
        Vec3 beltA = new(-beltLen / 2, 0, -2), beltB = new(beltLen / 2, 0, -2);
        var beltMesh = Mesh.GenerateCube(Vec3.One);
        var beltMat = Default.Material.Copy(); beltMat[MatParamName.ColorTint] = new Color(1f, 1f, 1f);
        var beltTrs = Matrix.TRS(new Vec3(0, -0.2f, -2), Quat.Identity, new Vec3(beltLen, beltH, beltW));
        var floorMesh = Mesh.GenerateCube(Vec3.One);
        var floorMat = Default.Material.Copy(); floorMat[MatParamName.ColorTint] = new Color(0.4f, 0.4f, 0.4f);
        var floorTrs = Matrix.TRS(new Vec3(0, -0.1f, -2), Quat.Identity, new Vec3(beltLen, 0.1f, 3));
        var style1 = Text.MakeStyle(Font.Default, 0.5f, Color.White);

        float speed = 0.3f, minGapM = 0.5f, spawnGapM = 10f;
        float minGapT = minGapM / beltLen, spawnGapT = spawnGapM / beltLen;

        int inCount = 0; string last = "-";
        Pose cam = new(new Vec3(0, 3f, 2f), Quat.LookDir(new Vec3(0, 0, -1f)));
        bool paused = false;

        float fps = 0, accumTime = 0; int frameCount = 0;

        SK.Run(() =>
        {
            float dtt = Time.Elapsedf;
            accumTime += dtt; frameCount++;
            if (accumTime >= 1.0f) { fps = frameCount / accumTime; accumTime = 0; frameCount = 0; }

            if (Input.Key(Key.Space).IsJustActive()) paused = !paused;

            //sound
            bgInst.Position = Input.Head.position;

            //if sound finishes restart
            if (!bgInst.IsPlaying)
                bgInst = bg.Play(Input.Head.position, 0.35f);

            // === KUYRUK -> YENI ARABA ===
            while (mqttQueue.TryDequeue(out var ev))
            {
                inCount++;

                string modelId = DecideModelId(ev.carFamily, ev.katashiki);
                last = $"{ev.bodyNo}/{modelId}/{ev.colorExtCode}";

                // Renk kodunu temiz kullan (TryParseVehEvent zaten trim + boşluk silme yapıyor)
                Color body = ColorMap.TryGetValue(ev.colorExtCode ?? "", out var c) ? c : new Color(0, 0, 0);
                Color black = new Color(0, 0, 0);
                Model[] set;

                if (modelId.Equals("x94W", StringComparison.OrdinalIgnoreCase))
                {
                    // Corolla
                    set = MakeCorollaSetColored(body, baseCorolla);
                }
                else if (modelId.Equals("x00W", StringComparison.OrdinalIgnoreCase))
                {
                    // C-HR -> katashiki 'M' ile başlıyorsa tavan/siyah ikincil yap
                    if (!string.IsNullOrEmpty(ev.katashiki) &&
                        ev.katashiki.TrimStart().StartsWith("M", StringComparison.OrdinalIgnoreCase))
                    {
                        set = MakeChrSetColored(body, black, black, baseChr); // chr3 benzeri
                    }
                    else
                    {
                        set = MakeChrSetColored(body, body, body, baseChr);   // chr1 benzeri
                    }
                }
                else
                {
                    // Default Corolla
                    set = MakeCorollaSetColored(body, baseCorolla);
                }

                float t0;
                if (cars.Count > 0)
                    t0 = MathF.Min(cars[^1].t - spawnGapT, -spawnGapT);
                else
                    t0 = 0f; // ilk araba hemen bantın başında görünsün

                cars.Add(new CarQ { modelSet = set, modelIdx = 0, t = t0, stopped = false });
            }

            // Statikler
            background.Draw((Matrix.TRS(new Vec3(39f, -1f, 2f), Quat.FromAngles(180, -90, 180), Vec3.One * 0.75f)) * (Matrix.S(new Vec3(-1, 1, -1))));
            // floorMesh.Draw(floorMat, floorTrs);
            beltMesh.Draw(beltMat, beltTrs);
            Text.Add("Kaput", Matrix.T(25f, 2f, -2.5f) * Matrix.S(new Vec3(-1, 1, 1)), style1, TextAlign.Center);
            Text.Add("Far", Matrix.T(15f, 2f, -2.5f) * Matrix.S(new Vec3(-1, 1, 1)), style1, TextAlign.Center);
            Text.Add("Tampon", Matrix.T(5f, 2f, -2.5f) * Matrix.S(new Vec3(-1, 1, 1)), style1, TextAlign.Center);

            // İlerleme
            float dt = Time.Elapsedf;
            float dT = (speed / beltLen) * dt;
            bool isHalted = paused || beltStop;

            if (!isHalted && cars.Count > 0)
            {
                if (!cars[0].stopped) { cars[0].t += dT; if (cars[0].t >= 1f) { cars[0].t = 1f; cars[0].stopped = true; } }
                for (int i = 1; i < cars.Count; i++) if (!cars[i].stopped)
                    {
                        cars[i].t += dT;
                        float maxT = MathF.Min(1f, cars[i - 1].t - minGapT);
                        if (cars[i].t > maxT) cars[i].t = maxT;
                        if (cars[i].t >= 1f) { cars[i].t = 1f; cars[i].stopped = true; }
                    }
                while (cars.Count > 0 && cars[0].t >= 1f) cars.RemoveAt(0);
            }

            // Çizim + istasyon geçişleri
            Vec3 dir = (beltB - beltA).Normalized;
            foreach (var c in cars)
            {
                Vec3 pos = (c.t <= 0f) ? beltA + dir * (c.t * beltLen) : Vec3.Lerp(beltA, beltB, Math.Clamp(c.t, 0f, 1f));
                pos.y = 0.1f; Quat rot = Quat.FromAngles(0, -90, 0);
                int newIdx = 0; float x = pos.x;
                float[] cuts = { -30f, -20f, -10f };
                for (int k = 0; k < cuts.Length; k++) if (x > cuts[k]) newIdx = k + 1;
                if (newIdx >= c.modelSet.Length) newIdx = c.modelSet.Length - 1;
                if (newIdx != c.modelIdx)
                {
                    c.modelIdx = newIdx;
                    if (c.modelSet[c.modelIdx].Anims.Count > 0)
                        c.modelSet[c.modelIdx].PlayAnim(c.modelSet[c.modelIdx].Anims[0], AnimMode.Once);
                }
                c.modelSet[c.modelIdx].Draw(Matrix.TRS(pos, rot, Vec3.One * 0.8f));
            }

            // HUD


            Text.Add($"Cars:{cars.Count}  In:{inCount}  Last:{last}",
                     Matrix.T(33f, 5f, -2.5f) * Matrix.S(new Vec3(-1, 1, 1)),
                     Text.MakeStyle(Font.Default, 0.35f, Color.White), TextAlign.TopLeft);
            Text.Add($"FPS: {fps:F1}",
                     Matrix.T(30f, 4.2f, -2.5f) * Matrix.S(new Vec3(-1, 1, 1)),
                     Text.MakeStyle(Font.Default, 0.35f, Color.White), TextAlign.TopLeft);

            // Kamera / el hareketi
            Vec2 left = Input.Controller(Handed.Left).stick;
            Vec2 right = Input.Controller(Handed.Right).stick;
            float moveSpeed = 2f * dt;

            Vec3 forward = Input.Head.Forward; forward.y = 0;
            if (forward.Magnitude < 1e-6f) forward = new Vec3(0, 0, -1); else forward = forward.Normalized;
            Vec3 rightDir = Vec3.Cross(new Vec3(0, 1, 0), forward).Normalized;

            Vec3 moveLocal = forward * left.y * moveSpeed + rightDir * left.x * moveSpeed;
            moveLocal += new Vec3(0, right.y * moveSpeed, 0);

            Controller leftController = Input.Controller(Handed.Left);
            if (leftController.IsX1Pressed)
            {
                Vec3 beltDir = (beltB - beltA).Normalized;
                float autoSpeed = latestBeltSpeed * dt * 0.3f;
                moveLocal += beltDir * autoSpeed;
            }


            if (Input.Key(Key.W).IsActive()) moveLocal += forward * moveSpeed;
            if (Input.Key(Key.S).IsActive()) moveLocal -= forward * moveSpeed;
            if (Input.Key(Key.A).IsActive()) moveLocal += rightDir * moveSpeed;
            if (Input.Key(Key.D).IsActive()) moveLocal -= rightDir * moveSpeed;
            if (Input.Key(Key.Q).IsActive()) moveLocal += new Vec3(0, moveSpeed, 0);
            if (Input.Key(Key.E).IsActive()) moveLocal -= new Vec3(0, moveSpeed, 0);

            cam.position += moveLocal;
            if (cam.position.y < 0.2f) cam.position.y = 0.2f;
            Renderer.CameraRoot = cam.ToMatrix();

            // Bileklik / HUD ekleri (isteğe bağlı)

        });

        SK.Shutdown();
    }
}

/*
Örnek payload (artık boşluklara/trimlere toleranslı):
{
  "bodyNo": 37365,
  "katashiki": "ZWE219L-DEXNBW      ",
  "colorExtCode": " 1K6",
  "vinNo": "NMTBD3BE90Rxxxxxx",
  "carFamily": "x94W",
  "loDate": "20250911"
}
*/
