using Godot;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;

public partial class Main : Node2D
{
	private Camera2D _camera;
	private HttpRequest _http;
	private bool _isDragging = false;
	private Texture2D _roadEWTexture;
	private Texture2D _roadTileTexture;
	private readonly Dictionary<string, Texture2D> _roadTextures = new();
	private readonly Dictionary<string, int> _facilityPatientCounts = new();
	private readonly Dictionary<string, string> _lastPreferredFacility = new();

	private const int GridSize = 20;
	private const int TileSpacing = 64;

	// Road neighbor bit flags (N/E/S/W)
	private const int RoadN = 1;
	private const int RoadE = 2;
	private const int RoadS = 4;
	private const int RoadW = 8;

	private const int HospitalCount = 2;
	private const int HouseCount = 4;

	private const float RoadCost = 3f;
	private const float MallCost = 5f;
	private const float RoadDestroyCost = 10f;
	private const float UpgradeCost = 2f;
	private const float BlockageCost = 4f;
	private const float CutFundingCost = 8f;

	private const float RevenuePerPatient = 12f;
	private const float DailyClinicUpkeep = 10f;
	private const float DailyUpkeepGrowth = 0.5f;

	private readonly HashSet<Vector2I> _occupiedTiles = new();
	private readonly List<Vector2I> _hospitalPositions = new();
	private readonly List<Vector2I> _housePositions = new();
	private readonly Dictionary<Vector2I, BuildTool> _structures = new();
	private readonly Dictionary<Vector2I, Control> _structureNodes = new();
	private readonly HashSet<Vector2I> _upgradedRoads = new();
	private readonly HashSet<Vector2I> _blockedRoads = new();
	private readonly HashSet<Vector2I> _optimizerRoads = new(); // roads built by city
	private readonly List<Line2D> _routeLines = new();

	// Sea wave animation
	private readonly List<ColorRect> _waveRects = new();
	private float _waveTime = 0f;

	// Loading screen
	private CanvasLayer _loadingLayer;
	private Label _loadingDotsLabel;
	private ColorRect _loadingBar;
	private ColorRect _loadingBarFill;
	private float _loadingTime = 0f;
	private bool _loadingVisible = false;

	private Vector2I _clinicPos;

	// Game state
	private int _day = 0;
	private float _playerBudget = 50f;
	private float _citizenBudget = 30f;
	private float _clinicMoney = 50f;
	private float _previousDayProfit = 0f;
	private float _peakCoverage = 0f;
	private float _totalRevenue = 0f;

	private bool _hospitalFundingCutActive = false;
	private bool _fundingCutUsedThisTurn = false;
	private bool _budgetLossPending = false;

	private bool _turnInProgress = false;
	private bool _resolveTurnAfterResponse = false;
	private bool _gameOver = false;

	private Vector2I _lastHoveredTile = new(-1, -1);
	private float _hoverTimer = 0f;

	private Label _dayLabel;
	private Label _playerBudgetLabel;
	private Label _optimizerBudgetLabel;
	private Label _previousProfitLabel;
	private Label _coverageLabel;
	private Label _toolLabel;
	private Label _statusLabel;
	private PanelContainer _hoverTooltip;
	private Label _hoverTooltipLabel;

	private PanelContainer _playerStatsPanel;
	private PanelContainer _cityStatsPanel;
	private PanelContainer _clinicStatsPanel;

	private PanelContainer _actionsPanel;
	private ColorRect _clinicHealthBar;
	private ColorRect _clinicHealthBarBg;

	private Button _pointerButton;
	private Button _roadBuildButton;
	private Button _roadDestroyButton;
	private Button _roadUpgradeButton;
	private Button _mallBuildButton;
	private Button _mallDestroyButton;
	private Button _cutHospitalFundingButton;
	private Button _roadBlockageButton;
	private Button _endTurnButton;

	private BuildTool _selectedTool = BuildTool.None;

	private enum BuildTool
	{
		None,
		Road,
		RoadDestroy,
		RoadUpgrade,
		Mall,
		MallDestroy,
		RoadBlockage,
	}

	// -------------------------------------------------------------------------
	// _Ready
	// -------------------------------------------------------------------------
	public override void _Ready()
	{
		_camera = GetNode<Camera2D>("Camera2D");
		_camera.MakeCurrent();

		LoadRoadTextures();

		_http = new HttpRequest();
		AddChild(_http);
		_http.RequestCompleted += OnRequestCompleted;

		// Ocean background outside island/map.
		RenderingServer.SetDefaultClearColor(new Color(0.08f, 0.35f, 0.65f));

		GenerateGrid();
		GenerateWaves();
		CreateLoadingScreen();
		CreateHud();
		UpdateHud();
		SetSelectedTool(BuildTool.None);

		CallDeferred(nameof(SendInitialOptimizationRequest));
	}

	public override void _Process(double delta)
	{
		UpdateHudLayout();
		UpdateHoverStats(delta);
		AnimateWaves(delta);
		AnimateLoadingScreen(delta);
	}

	// -------------------------------------------------------------------------
	// Hover tooltip — floats over the tile in world space
	// -------------------------------------------------------------------------
	private void UpdateHoverStats(double delta)
	{
		if (_hoverTooltip == null) return;
		if (GetViewport().GuiGetHoveredControl() != null)
		{
			_hoverTooltip.Visible = false;
			_hoverTimer = 0f;
			return;
		}

		Vector2 mouseWorld = GetGlobalMousePosition();
		Vector2I gridPos = new(
			Mathf.FloorToInt(mouseWorld.X / TileSpacing),
			Mathf.FloorToInt(mouseWorld.Y / TileSpacing)
		);

		if (gridPos.X < 0 || gridPos.X >= GridSize || gridPos.Y < 0 || gridPos.Y >= GridSize)
		{
			_hoverTooltip.Visible = false;
			_hoverTimer = 0f;
			return;
		}

		if (gridPos == _lastHoveredTile)
		{
			_hoverTimer += (float)delta;
		}
		else
		{
			_lastHoveredTile = gridPos;
			_hoverTimer = 0f;
			_hoverTooltip.Visible = false;
		}

		if (_hoverTimer >= 0.4f)
		{
			string text = GetTileTooltipText(gridPos);
			if (string.IsNullOrEmpty(text))
			{
				_hoverTooltip.Visible = false;
				return;
			}

			_hoverTooltipLabel.Text = text;
			_hoverTooltip.Visible = true;

			// Position the tooltip above the tile in world space
			Vector2 tileWorldPos = new Vector2(gridPos.X * TileSpacing, gridPos.Y * TileSpacing);
			_hoverTooltip.Position = tileWorldPos + new Vector2(TileSpacing * 0.5f - 80f, -50f);
		}
	}

	private string GetTileTooltipText(Vector2I gridPos)
	{
		if (_structures.TryGetValue(gridPos, out var structure))
		{
			if (structure == BuildTool.Road)
			{
				float congestion = GetRoadCongestionValue(gridPos);
				bool upgraded = _upgradedRoads.Contains(gridPos);
				bool blocked = _blockedRoads.Contains(gridPos);
				bool isOptimizerRoad = _optimizerRoads.Contains(gridPos);
				string flags = "";
				if (upgraded) flags += " [UPGRADED]";
				if (blocked) flags += " [BLOCKED]";
				if (isOptimizerRoad) flags += " [CITY]";
				return $"Road{flags}\nCongestion: {congestion:0.00}";
			}
			if (structure == BuildTool.Mall)
			{
				float caused = GetMallCongestionCaused(gridPos);
				return $"Mall\n+{caused:0.0} congestion";
			}
		}

		int hospitalIdx = _hospitalPositions.IndexOf(gridPos);
		if (hospitalIdx >= 0)
		{
			int patients = GetFacilityPatients($"Hospital_{hospitalIdx}");
			return $"Hospital {hospitalIdx}\nPatients: {patients}";
		}

		if (gridPos == _clinicPos)
		{
			int patients = GetFacilityPatients("Clinic");
			float revenue = patients * RevenuePerPatient;
			return $"Uncle's Clinic ⭐\nPatients: {patients} | ${revenue:0.0}";
		}

		int houseIdx = _housePositions.IndexOf(gridPos);
		if (houseIdx >= 0)
		{
			string preferred = GetFacilityForHouse($"House_{houseIdx}");
			return $"House {houseIdx} 🏠\nPrefers: {preferred}";
		}

		return null;
	}

	private string GetFacilityForHouse(string houseId)
	{
		return _lastPreferredFacility.TryGetValue(houseId, out var facilityId)
			? facilityId
			: "unknown";
	}

	private float GetRoadCongestionValue(Vector2I roadPos)
	{
		float mallImpact = 0f;
		foreach (var kv in _structures)
		{
			if (kv.Value == BuildTool.Mall && Manhattan(kv.Key, roadPos) <= 1)
				mallImpact += 1.4f;
		}
		return mallImpact;
	}

	private float GetMallCongestionCaused(Vector2I mallPos)
	{
		int affectedRoads = 0;
		foreach (var kv in _structures)
		{
			if (kv.Value == BuildTool.Road && Manhattan(kv.Key, mallPos) <= 1)
				affectedRoads++;
		}
		return affectedRoads * 1.4f;
	}

	private int GetFacilityPatients(string facilityId)
	{
		return _facilityPatientCounts.TryGetValue(facilityId, out var count) ? count : 0;
	}

	private bool TryGetFacilityGrid(string facilityId, out Vector2I facilityGrid)
	{
		facilityGrid = default;
		if (facilityId == "Clinic")
		{
			facilityGrid = _clinicPos;
			return true;
		}

		if (!string.IsNullOrEmpty(facilityId) &&
			facilityId.StartsWith("Hospital_") &&
			int.TryParse(facilityId.Replace("Hospital_", ""), out int hospitalIdx) &&
			hospitalIdx >= 0 &&
			hospitalIdx < _hospitalPositions.Count)
		{
			facilityGrid = _hospitalPositions[hospitalIdx];
			return true;
		}

		return false;
	}

	private HashSet<Vector2I> BuildWalkableTiles()
	{
		var roadTiles = new HashSet<Vector2I>();
		foreach (var kv in _structures)
		{
			if (kv.Value == BuildTool.Road && !_blockedRoads.Contains(kv.Key))
				roadTiles.Add(kv.Key);
		}

		var buildingConnectors = new HashSet<Vector2I>();
		foreach (var pos in _occupiedTiles)
		{
			buildingConnectors.Add(pos);
			foreach (var next in GetCardinalNeighbors(pos))
			{
				if (next.X >= 0 && next.Y >= 0 && next.X < GridSize && next.Y < GridSize)
					buildingConnectors.Add(next);
			}
		}

		var walkable = new HashSet<Vector2I>(roadTiles);
		foreach (var pos in _occupiedTiles)
			walkable.Add(pos);

		foreach (var tile in buildingConnectors)
		{
			if (walkable.Contains(tile)) continue;
			foreach (var next in GetCardinalNeighbors(tile))
			{
				if (roadTiles.Contains(next))
				{
					walkable.Add(tile);
					break;
				}
			}
		}

		return walkable;
	}

	private Dictionary<Vector2I, int> ComputeWalkableDistances(Vector2I start, HashSet<Vector2I> walkable)
	{
		var distances = new Dictionary<Vector2I, int>();
		if (!walkable.Contains(start)) return distances;

		var queue = new Queue<Vector2I>();
		queue.Enqueue(start);
		distances[start] = 0;

		while (queue.Count > 0)
		{
			var pos = queue.Dequeue();
			int cost = distances[pos];
			foreach (var next in GetCardinalNeighbors(pos))
			{
				if (next.X < 0 || next.Y < 0 || next.X >= GridSize || next.Y >= GridSize) continue;
				if (!walkable.Contains(next) || distances.ContainsKey(next)) continue;
				distances[next] = cost + 1;
				queue.Enqueue(next);
			}
		}

		return distances;
	}

	private IEnumerable<Vector2I> GetCardinalNeighbors(Vector2I pos)
	{
		yield return new Vector2I(pos.X, pos.Y - 1);
		yield return new Vector2I(pos.X + 1, pos.Y);
		yield return new Vector2I(pos.X, pos.Y + 1);
		yield return new Vector2I(pos.X - 1, pos.Y);
	}

	private Dictionary<string, string> SanitizePreferredFacilities(Dictionary<string, string> preferredFacility)
	{
		var sanitized = new Dictionary<string, string>();
		_lastPreferredFacility.Clear();

		if (_housePositions.Count == 0)
			return sanitized;

		var walkable = BuildWalkableTiles();
		var facilityIds = new List<string> { "Clinic" };
		for (int i = 0; i < _hospitalPositions.Count; i++)
			facilityIds.Add($"Hospital_{i}");

		for (int i = 0; i < _housePositions.Count; i++)
		{
			string houseId = $"House_{i}";
			Vector2I houseGrid = _housePositions[i];
			string assignedFacility = null;
			preferredFacility?.TryGetValue(houseId, out assignedFacility);

			var distances = ComputeWalkableDistances(houseGrid, walkable);
			string bestFacility = "Disconnected";
			int bestDistance = int.MaxValue;

			if (!string.IsNullOrEmpty(assignedFacility) &&
				assignedFacility != "Disconnected" &&
				TryGetFacilityGrid(assignedFacility, out var assignedGrid) &&
				distances.ContainsKey(assignedGrid))
			{
				bestFacility = assignedFacility;
			}
			else
			{
				foreach (string facilityId in facilityIds)
				{
					if (!TryGetFacilityGrid(facilityId, out var facilityGrid)) continue;
					if (!distances.TryGetValue(facilityGrid, out int distance)) continue;

					int score = distance;
					if (facilityId == "Clinic")
						score += 1; // mirror the clinic's small baseline penalty

					if (score < bestDistance)
					{
						bestDistance = score;
						bestFacility = facilityId;
					}
				}
			}

			sanitized[houseId] = bestFacility;
			_lastPreferredFacility[houseId] = bestFacility;
		}

		return sanitized;
	}

	// -------------------------------------------------------------------------
	// Sea wave animation
	// -------------------------------------------------------------------------
	private void GenerateWaves()
	{
		int gridPixels = GridSize * TileSpacing;
		int margin     = 4 * TileSpacing;
		int waveRows   = 5;
		int waveH      = TileSpacing;

		for (int i = 0; i < waveRows; i++)  // top
			_waveRects.Add(MakeWaveRect(new Rect2(-margin, -(i + 1) * waveH, gridPixels + margin * 2, waveH)));
		for (int i = 0; i < waveRows; i++)  // bottom
			_waveRects.Add(MakeWaveRect(new Rect2(-margin, gridPixels + i * waveH, gridPixels + margin * 2, waveH)));
		for (int i = 0; i < waveRows; i++)  // left
			_waveRects.Add(MakeWaveRect(new Rect2(-(i + 1) * waveH, 0, waveH, gridPixels)));
		for (int i = 0; i < waveRows; i++)  // right
			_waveRects.Add(MakeWaveRect(new Rect2(gridPixels + i * waveH, 0, waveH, gridPixels)));
	}

	private ColorRect MakeWaveRect(Rect2 rect)
	{
		var cr = new ColorRect
		{
			Position    = rect.Position,
			Size        = rect.Size,
			Color       = new Color(0.05f, 0.30f, 0.62f, 0.72f),
			ZIndex      = -1,
			MouseFilter = Control.MouseFilterEnum.Ignore,
		};
		AddChild(cr);
		return cr;
	}

	private void AnimateWaves(double delta)
	{
		_waveTime += (float)delta;
		for (int i = 0; i < _waveRects.Count; i++)
		{
			float phase  = _waveTime * 0.8f + i * 0.55f;
			float alpha  = 0.55f + 0.22f * Mathf.Sin(phase);
			float bright = 0.28f + 0.10f * Mathf.Cos(phase * 0.7f);
			_waveRects[i].Color = new Color(0.04f, bright, 0.58f + bright * 0.2f, alpha);
		}
	}

	// -------------------------------------------------------------------------
	// Loading screen
	// -------------------------------------------------------------------------
	private void CreateLoadingScreen()
	{
		_loadingLayer = new CanvasLayer { Layer = 200, Name = "LoadingLayer" };
		AddChild(_loadingLayer);

		var overlay = new ColorRect
		{
			Color         = new Color(0.04f, 0.06f, 0.12f, 0.97f),
			AnchorsPreset = (int)Control.LayoutPreset.FullRect,
			MouseFilter   = Control.MouseFilterEnum.Stop,
		};
		_loadingLayer.AddChild(overlay);

		var panel = new VBoxContainer
		{
			AnchorsPreset     = (int)Control.LayoutPreset.Center,
			GrowHorizontal    = Control.GrowDirection.Both,
			GrowVertical      = Control.GrowDirection.Both,
			CustomMinimumSize = new Vector2(480, 260),
		};
		panel.AddThemeConstantOverride("separation", 18);
		_loadingLayer.AddChild(panel);

		var title = new Label { Text = "🏥 Clinic Survival", HorizontalAlignment = HorizontalAlignment.Center };
		title.AddThemeFontSizeOverride("font_size", 42);
		title.Modulate = new Color(0.9f, 0.75f, 0.25f);
		panel.AddChild(title);

		var sub = new Label { Text = "City-Building Optimization", HorizontalAlignment = HorizontalAlignment.Center };
		sub.AddThemeFontSizeOverride("font_size", 16);
		sub.Modulate = new Color(0.7f, 0.85f, 1.0f);
		panel.AddChild(sub);

		panel.AddChild(new Control { CustomMinimumSize = new Vector2(0, 10) });

		_loadingBar = new ColorRect { CustomMinimumSize = new Vector2(440, 8), Color = new Color(0.15f, 0.18f, 0.28f) };
		panel.AddChild(_loadingBar);

		_loadingBarFill = new ColorRect { Size = new Vector2(0, 8), Color = new Color(0.2f, 0.75f, 1.0f), ZIndex = 1 };
		_loadingBar.AddChild(_loadingBarFill);

		_loadingDotsLabel = new Label { Text = "Solving optimal routes", HorizontalAlignment = HorizontalAlignment.Center };
		_loadingDotsLabel.AddThemeFontSizeOverride("font_size", 14);
		_loadingDotsLabel.Modulate = new Color(0.8f, 0.85f, 0.95f);
		panel.AddChild(_loadingDotsLabel);

		var pw = new Label { Text = "Powered by GAMSPy LP Optimizer", HorizontalAlignment = HorizontalAlignment.Center };
		pw.AddThemeFontSizeOverride("font_size", 11);
		pw.Modulate = new Color(0.45f, 0.55f, 0.70f);
		panel.AddChild(pw);

		_loadingVisible = true;
	}

	private void HideLoadingScreen()
	{
		if (!_loadingVisible || _loadingLayer == null) return;
		_loadingVisible = false;
		var tween = CreateTween();
		tween.TweenProperty(_loadingLayer, "modulate", new Color(1, 1, 1, 0), 0.5f);
		tween.TweenCallback(Callable.From(() => _loadingLayer.QueueFree()));
	}

	private void AnimateLoadingScreen(double delta)
	{
		if (!_loadingVisible || _loadingLayer == null) return;
		_loadingTime += (float)delta;

		int dotCount = (int)(_loadingTime * 2.0f) % 4;
		if (_loadingDotsLabel != null)
			_loadingDotsLabel.Text = $"Solving optimal routes{new string('.', dotCount)}";

		if (_loadingBar != null && _loadingBarFill != null)
		{
			float totalW = _loadingBar.Size.X > 0 ? _loadingBar.Size.X : 440f;
			float t      = (_loadingTime % 1.6f) / 1.6f;
			float fillW  = totalW * Mathf.Sin(t * Mathf.Pi);
			_loadingBarFill.Size  = new Vector2(fillW, 8f);
			_loadingBarFill.Color = new Color(0.1f + t * 0.1f, 0.65f + t * 0.3f, 1.0f);
		}
	}

	// -------------------------------------------------------------------------
	// Map generation
	// -------------------------------------------------------------------------
	private void GenerateGrid()
	{
		_occupiedTiles.Clear();
		_hospitalPositions.Clear();
		_housePositions.Clear();
		_structures.Clear();
		_structureNodes.Clear();
		_upgradedRoads.Clear();
		_blockedRoads.Clear();
		_optimizerRoads.Clear();

		var rng = new RandomNumberGenerator();
		rng.Randomize();

		// Ground tiles
		for (int x = 0; x < GridSize; x++)
		{
			for (int y = 0; y < GridSize; y++)
			{
				// Lighter, more vibrant grass with subtle variation
				float shade = 0.58f + (((x * 7 + y * 3) % 10) * 0.018f);
				float warm  = 0.25f + (((x * 3 + y * 5) % 6) * 0.008f);
				Color groundColor = rng.Randf() < 0.06f
					? new Color(0.52f, 0.50f, 0.44f)   // warm stone patch
					: new Color(warm, shade, 0.24f);    // fresh spring green
				PlaceTile(new Vector2I(x, y), groundColor, $"Tile_{x}_{y}");
			}
		}

		// Clinic — center
		_clinicPos = new Vector2I(GridSize / 2, GridSize / 2);
		PlaceEntity(_clinicPos, new Color(0.85f, 0.65f, 0.1f), "⭐", "Clinic");
		_occupiedTiles.Add(_clinicPos);

		// Hospitals — spread across diagonal corner sectors
		PlaceHospitalsInSectors(rng);

		// Houses — guarantee at least 1 per hospital Voronoi region
		PlaceHousesWithCoverage(rng);
	}

	private void PlaceHospitalsInSectors(RandomNumberGenerator rng)
	{
		// Four corner sectors far from center (GridSize=20, center=10)
		var sectors = new (int x0, int y0, int x1, int y1)[]
		{
			(2, 2, 7, 7),           // NW
			(13, 2, 18, 7),         // NE
			(2, 13, 7, 18),         // SW
			(13, 13, 18, 18),       // SE
		};

		// Choose a diagonal pair for maximum spread: NW+SE or NE+SW
		int[] chosen = rng.Randf() > 0.5f ? new[] { 0, 3 } : new[] { 1, 2 };

		for (int h = 0; h < HospitalCount && h < chosen.Length; h++)
		{
			var (x0, y0, x1, y1) = sectors[chosen[h]];
			PlaceInRect(rng, x0, y0, x1, y1,
				new Color(0.75f, 0.18f, 0.18f), "🏥", $"Hospital_{h}",
				_hospitalPositions);
		}
	}

	private void PlaceInRect(RandomNumberGenerator rng, int x0, int y0, int x1, int y1,
		Color col, string emoji, string nodeName, List<Vector2I> storeList)
	{
		for (int attempt = 0; attempt < 500; attempt++)
		{
			Vector2I pos = new(rng.RandiRange(x0, x1), rng.RandiRange(y0, y1));
			if (_occupiedTiles.Contains(pos)) continue;
			PlaceEntity(pos, col, emoji, nodeName);
			_occupiedTiles.Add(pos);
			storeList.Add(pos);
			return;
		}
		GD.PrintErr($"PlaceInRect: could not place {nodeName} in ({x0},{y0})-({x1},{y1})");
	}

	private void PlaceHousesWithCoverage(RandomNumberGenerator rng)
	{
		var allExclusions = new List<Vector2I>(_hospitalPositions) { _clinicPos };
		var hospitalCoverage = new int[_hospitalPositions.Count]; // # houses closer to each hospital than clinic

		// ---- Random house placement ----
		int placed = 0;
		for (int attempt = 0; attempt < 3000 && placed < HouseCount; attempt++)
		{
			Vector2I pos = new(rng.RandiRange(2, GridSize - 3), rng.RandiRange(2, GridSize - 3));
			if (_occupiedTiles.Contains(pos)) continue;
			if (allExclusions.Exists(ex => Distance(pos, ex) < 3f)) continue;

			int closest = ClosestHospitalIndex(pos);
			PlaceEntity(pos, new Color(0.2f, 0.38f, 0.75f), "🏠", $"House_{placed}");
			_occupiedTiles.Add(pos);
			_housePositions.Add(pos);
			if (closest >= 0 && Distance(pos, _hospitalPositions[closest]) < Distance(pos, _clinicPos))
				hospitalCoverage[closest]++;
			placed++;
		}

		// ---- Guarantee: force at least 1 house closer to each hospital ----
		for (int h = 0; h < _hospitalPositions.Count; h++)
		{
			if (hospitalCoverage[h] > 0) continue;

			Vector2I hosp = _hospitalPositions[h];
			for (int attempt = 0; attempt < 1000; attempt++)
			{
				int dx = rng.RandiRange(-5, 5);
				int dy = rng.RandiRange(-5, 5);
				if (dx == 0 && dy == 0) continue;
				Vector2I pos = new(
					Mathf.Clamp(hosp.X + dx, 2, GridSize - 3),
					Mathf.Clamp(hosp.Y + dy, 2, GridSize - 3));
				if (_occupiedTiles.Contains(pos)) continue;
				if (allExclusions.Exists(ex => Distance(pos, ex) < 2f)) continue;
				if (Distance(pos, hosp) >= Distance(pos, _clinicPos)) continue; // must be closer to hospital

				string nodeName = $"House_{_housePositions.Count}";
				PlaceEntity(pos, new Color(0.2f, 0.38f, 0.75f), "🏠", nodeName);
				_occupiedTiles.Add(pos);
				_housePositions.Add(pos);
				hospitalCoverage[h]++;
				break;
			}
		}
	}

	private int ClosestHospitalIndex(Vector2I pos)
	{
		int best = -1;
		float bestDist = float.MaxValue;
		for (int i = 0; i < _hospitalPositions.Count; i++)
		{
			float d = Distance(pos, _hospitalPositions[i]);
			if (d < bestDist) { bestDist = d; best = i; }
		}
		return best;
	}

	private void PlaceRandomEntities(
		RandomNumberGenerator rng,
		int count,
		float minDist,
		List<Vector2I> exclusions,
		Color col,
		string emoji,
		string prefix,
		List<Vector2I> storeList
	)
	{
		int placed = 0;
		int attempts = 0;

		while (placed < count && attempts < 2000)
		{
			attempts++;
			Vector2I pos = new(
				rng.RandiRange(2, GridSize - 3),
				rng.RandiRange(2, GridSize - 3)
			);

			if (_occupiedTiles.Contains(pos))
				continue;

			bool tooClose = false;
			if (exclusions != null)
			{
				foreach (var ex in exclusions)
				{
					if (Distance(pos, ex) < minDist)
					{
						tooClose = true;
						break;
					}
				}
			}

			if (!tooClose)
			{
				string nodeName = $"{prefix}_{placed}";
				PlaceEntity(pos, col, emoji, nodeName);
				_occupiedTiles.Add(pos);
				storeList.Add(pos);
				placed++;
			}
		}
	}

	private void PlaceTile(Vector2I gridPos, Color color, string name)
	{
		var rect = new ColorRect
		{
			Size = new Vector2(TileSpacing - 2, TileSpacing - 2),
			Position = new Vector2(gridPos.X * TileSpacing, gridPos.Y * TileSpacing),
			Color = color,
			Name = name,
			MouseFilter = Control.MouseFilterEnum.Ignore,
			ZIndex = 0
		};
		AddChild(rect);
	}

	private void PlaceEntity(Vector2I gridPos, Color bgColor, string emoji, string nodeName)
	{
		var rect = new ColorRect
		{
			Size = new Vector2(TileSpacing - 2, TileSpacing - 2),
			Position = new Vector2(gridPos.X * TileSpacing, gridPos.Y * TileSpacing),
			Color = bgColor,
			Name = nodeName,
			ZIndex = 1,
			MouseFilter = Control.MouseFilterEnum.Ignore
		};

		var label = new Label
		{
			Text = emoji,
			Size = rect.Size,
			HorizontalAlignment = HorizontalAlignment.Center,
			VerticalAlignment = VerticalAlignment.Center
		};
		label.AddThemeFontSizeOverride("font_size", 32);

		rect.AddChild(label);
		AddChild(rect);
	}

	// -------------------------------------------------------------------------
	// Structure placement / removal
	// -------------------------------------------------------------------------
	private void PlaceStructure(Vector2I gridPos, BuildTool tool, bool animated = false, bool isOptimizerRoad = false)
	{
		_structures[gridPos] = tool;

		if (tool == BuildTool.Road)
		{
			if (isOptimizerRoad)
				_optimizerRoads.Add(gridPos);

			RefreshRoadAt(gridPos);
			RefreshRoadAt(new Vector2I(gridPos.X, gridPos.Y - 1));
			RefreshRoadAt(new Vector2I(gridPos.X + 1, gridPos.Y));
			RefreshRoadAt(new Vector2I(gridPos.X, gridPos.Y + 1));
			RefreshRoadAt(new Vector2I(gridPos.X - 1, gridPos.Y));

			if (animated && _structureNodes.TryGetValue(gridPos, out var roadNode))
				AnimatePlacement(roadNode);
			return;
		}

		RemoveStructureNode(gridPos);

		var rect = new ColorRect
		{
			Size = new Vector2(TileSpacing - 14, TileSpacing - 14),
			Position = new Vector2(gridPos.X * TileSpacing + 6, gridPos.Y * TileSpacing + 6),
			Color = new Color(0.72f, 0.38f, 0.12f, 0.95f),
			Name = $"Structure_{gridPos.X}_{gridPos.Y}",
			ZIndex = 2,
			MouseFilter = Control.MouseFilterEnum.Ignore
		};

		var label = new Label
		{
			Text = "🛍️",
			Size = rect.Size,
			HorizontalAlignment = HorizontalAlignment.Center,
			VerticalAlignment = VerticalAlignment.Center
		};
		label.AddThemeFontSizeOverride("font_size", 24);

		rect.AddChild(label);
		AddChild(rect);
		_structureNodes[gridPos] = rect;

		if (animated)
			AnimatePlacement(rect);
	}

	private void DestroyStructureAt(Vector2I gridPos, bool animated = false)
	{
		if (!_structures.ContainsKey(gridPos)) return;

		bool wasRoad = _structures[gridPos] == BuildTool.Road;
		_structures.Remove(gridPos);
		_upgradedRoads.Remove(gridPos);
		_blockedRoads.Remove(gridPos);
		_optimizerRoads.Remove(gridPos);

		if (animated && _structureNodes.TryGetValue(gridPos, out var node))
		{
			AnimateDestroy(node, () =>
			{
				_structureNodes.Remove(gridPos);
				if (wasRoad)
				{
					RefreshRoadNeighbors(gridPos);
				}
			});
		}
		else
		{
			RemoveStructureNode(gridPos);
			if (wasRoad)
				RefreshRoadNeighbors(gridPos);
		}
	}

	private void RefreshRoadNeighbors(Vector2I gridPos)
	{
		RefreshRoadAt(new Vector2I(gridPos.X, gridPos.Y - 1));
		RefreshRoadAt(new Vector2I(gridPos.X + 1, gridPos.Y));
		RefreshRoadAt(new Vector2I(gridPos.X, gridPos.Y + 1));
		RefreshRoadAt(new Vector2I(gridPos.X - 1, gridPos.Y));
	}

	// -------------------------------------------------------------------------
	// Animations
	// -------------------------------------------------------------------------
	private void AnimatePlacement(Control node)
	{
		if (node == null) return;
		node.PivotOffset = node.Size * 0.5f;
		node.Scale = new Vector2(0.1f, 0.1f);
		node.Modulate = new Color(1f, 1f, 1f, 0f);

		var tween = CreateTween();
		tween.SetParallel(true);
		tween.TweenProperty(node, "scale", Vector2.One, 0.25f).SetTrans(Tween.TransitionType.Back).SetEase(Tween.EaseType.Out);
		tween.TweenProperty(node, "modulate", new Color(1f, 1f, 1f, 1f), 0.15f);
	}

	private void AnimateDestroy(Control node, Action onComplete)
	{
		if (node == null) { onComplete?.Invoke(); return; }
		node.PivotOffset = node.Size * 0.5f;
		var tween = CreateTween();
		tween.SetParallel(true);
		tween.TweenProperty(node, "scale", new Vector2(0.05f, 0.05f), 0.2f).SetTrans(Tween.TransitionType.Back).SetEase(Tween.EaseType.In);
		tween.TweenProperty(node, "modulate", new Color(1f, 0.3f, 0.3f, 0f), 0.2f);
		tween.Chain();
		tween.TweenCallback(Callable.From(() =>
		{
			node.QueueFree();
			onComplete?.Invoke();
		}));
	}

	private void AnimateUpgrade(Control node)
	{
		if (node == null) return;
		node.PivotOffset = node.Size * 0.5f;
		var tween = CreateTween();
		// Flash green
		tween.TweenProperty(node, "modulate", new Color(0.4f, 1f, 0.5f, 1f), 0.12f);
		tween.TweenProperty(node, "modulate", Colors.White, 0.12f);
		tween.TweenProperty(node, "modulate", new Color(0.4f, 1f, 0.5f, 1f), 0.12f);
		tween.TweenProperty(node, "modulate", Colors.White, 0.12f);
	}

	private void AnimateBlock(Control node)
	{
		if (node == null) return;
		node.PivotOffset = node.Size * 0.5f;
		var tween = CreateTween();
		tween.TweenProperty(node, "modulate", new Color(1f, 0.8f, 0.2f, 1f), 0.15f);
		tween.TweenProperty(node, "modulate", Colors.White, 0.15f);
	}

	// -------------------------------------------------------------------------
	// Road rendering
	// -------------------------------------------------------------------------
	private void LoadRoadTextures()
	{
		_roadTextures.Clear();

		_roadEWTexture = GD.Load<Texture2D>("res://roads/roadEW.tga");
		if (_roadEWTexture == null)
		{
			GD.PrintErr("Missing road texture: res://roads/roadEW.tga");
			return;
		}

		_roadTileTexture = ToSingleTileTexture(_roadEWTexture);
		_roadTextures["EW"] = _roadTileTexture;

		var dir = DirAccess.Open("res://roads");
		if (dir == null)
			return;

		dir.ListDirBegin();
		while (true)
		{
			string file = dir.GetNext();
			if (string.IsNullOrEmpty(file)) break;
			if (dir.CurrentIsDir()) continue;

			// Godot 4 automatically creates .import files for recognized textures.
			// When iterating, we might see 'road.tga.import' instead of 'road.tga' in some builds.
			if (file.EndsWith(".import", StringComparison.OrdinalIgnoreCase))
			{
				file = file.Substring(0, file.Length - 7);
			}

			if (!file.StartsWith("road", StringComparison.OrdinalIgnoreCase)) continue;

			string ext = Path.GetExtension(file).ToLowerInvariant();
			if (ext != ".tga" && ext != ".png" && ext != ".webp") continue;

			string nameNoExt = Path.GetFileNameWithoutExtension(file);
			string suffix = nameNoExt.Length > 4 ? nameNoExt.Substring(4) : string.Empty;
			string key = NormalizeRoadKey(suffix);
			if (string.IsNullOrEmpty(key)) continue;

			// Load the original resource path (Godot handles pulling the mapped .ctex)
			var tex = GD.Load<Texture2D>($"res://roads/{file}");
			if (tex != null && !_roadTextures.ContainsKey(key))
			{
				_roadTextures[key] = ToSingleTileTexture(tex);
			}
		}
		dir.ListDirEnd();
	}

	private static Texture2D ToSingleTileTexture(Texture2D texture)
	{
		if (texture == null) return null;
		Vector2 size = texture.GetSize();
		int w = Mathf.RoundToInt(size.X);
		int h = Mathf.RoundToInt(size.Y);

		int tileW = (w % 3 == 0) ? w / 3 : w;
		int tileH = (h % 3 == 0) ? h / 3 : h;

		if (tileW == w && tileH == h && (w > TileSpacing || h > TileSpacing))
		{
			tileW = Mathf.Min(TileSpacing, w);
			tileH = Mathf.Min(TileSpacing, h);
		}

		if (tileW == w && tileH == h)
			return texture;

		return new AtlasTexture
		{
			Atlas = texture,
			Region = new Rect2((w - tileW) * 0.5f, (h - tileH) * 0.5f, tileW, tileH)
		};
	}

	private static string NormalizeRoadKey(string raw)
	{
		if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
		bool n = false, e = false, s = false, w = false;
		string upper = raw.Trim().ToUpperInvariant();
		var token = new StringBuilder();
		void FlushToken()
		{
			if (token.Length == 0) return;
			ApplyRoadToken(token.ToString(), ref n, ref e, ref s, ref w);
			token.Clear();
		}
		foreach (char c in upper)
		{
			if (char.IsLetterOrDigit(c)) token.Append(c);
			else FlushToken();
		}
		FlushToken();

		bool onlyDirs = true;
		foreach (char c in upper)
		{
			if (c == '_' || c == '-' || c == ' ') continue;
			if (c is not ('N' or 'E' or 'S' or 'W')) { onlyDirs = false; break; }
		}
		if (!n && !e && !s && !w && onlyDirs)
		{
			foreach (char c in upper)
			{
				if (c == 'N') n = true;
				else if (c == 'E') e = true;
				else if (c == 'S') s = true;
				else if (c == 'W') w = true;
			}
		}

		var sb = new StringBuilder(4);
		if (n) sb.Append('N');
		if (e) sb.Append('E');
		if (s) sb.Append('S');
		if (w) sb.Append('W');
		return sb.ToString();
	}

	private static void ApplyRoadToken(string t, ref bool n, ref bool e, ref bool s, ref bool w)
	{
		switch (t)
		{
			case "N": case "NORTH": n = true; return;
			case "E": case "EAST": e = true; return;
			case "S": case "SOUTH": s = true; return;
			case "W": case "WEST": w = true; return;
			case "NS": case "SN": case "V": case "VERT": case "VERTICAL": n = s = true; return;
			case "EW": case "WE": case "H": case "HOR": case "HORIZONTAL": e = w = true; return;
			case "NESW": case "CROSS": case "X": case "PLUS": case "INTERSECTION": n = e = s = w = true; return;
			case "TN": n = e = w = true; return; // Standard Kenney T-junction pointing North (missing South)
			case "TS": s = e = w = true; return; // Missing North
			case "TE": n = s = e = true; return; // Missing West
			case "TW": n = s = w = true; return; // Missing East
		}
		foreach (char c in t)
		{
			if (c == 'N') n = true;
			else if (c == 'E') e = true;
			else if (c == 'S') s = true;
			else if (c == 'W') w = true;
		}
	}

	private void RefreshRoadAt(Vector2I gridPos)
	{
		if (!_structures.TryGetValue(gridPos, out var tool) || tool != BuildTool.Road)
			return;

		RemoveStructureNode(gridPos);
		int mask = GetRoadMask(gridPos);

		Control roadNode;
		if (TryGetRoadTexture(mask, out var tex, out float rotation))
		{
			var road = new TextureRect
			{
				Name = $"Structure_{gridPos.X}_{gridPos.Y}",
				Position = new Vector2(gridPos.X * TileSpacing, gridPos.Y * TileSpacing),
				Size = new Vector2(TileSpacing, TileSpacing),
				Texture = tex,
				StretchMode = TextureRect.StretchModeEnum.Scale,
				ZIndex = 2,
				MouseFilter = Control.MouseFilterEnum.Ignore
			};
			if (!Mathf.IsZeroApprox(rotation))
			{
				road.PivotOffset = road.Size * 0.5f;
				road.Rotation = rotation;
			}
			roadNode = road;
		}
		else
		{
			roadNode = CreateConnectedRoadFallback(gridPos, mask);
		}

		// Tint: optimizer roads are slightly blue, upgraded roads are slightly green
		if (_optimizerRoads.Contains(gridPos))
			roadNode.Modulate = new Color(0.7f, 0.85f, 1f);
		else if (_upgradedRoads.Contains(gridPos))
			roadNode.Modulate = new Color(0.75f, 1f, 0.75f);
		else if (_blockedRoads.Contains(gridPos))
		{
			roadNode.Modulate = new Color(1f, 0.6f, 0.2f);
			// Overlay 🚧 emoji so the blockage is visible at a glance
			var blockLabel = new Label
			{
				Text                = "🚧",
				Position            = new Vector2(gridPos.X * TileSpacing + 10, gridPos.Y * TileSpacing + 8),
				ZIndex              = 5,
				MouseFilter         = Control.MouseFilterEnum.Ignore,
			};
			blockLabel.AddThemeFontSizeOverride("font_size", 28);
			AddChild(blockLabel);
			// Store it alongside the road node so it's cleaned up on RefreshRoadAt
			// We attach it as metadata so RemoveStructureNode can find it
			roadNode.SetMeta("block_label_path", blockLabel.GetPath());
		}

		AddChild(roadNode);
		_structureNodes[gridPos] = roadNode;
	}

	private void RemoveStructureNode(Vector2I gridPos)
	{
		if (_structureNodes.TryGetValue(gridPos, out var oldNode))
		{
			// If this road had a blockage emoji label attached, free it too
			if (oldNode.HasMeta("block_label_path"))
			{
				var labelPath = oldNode.GetMeta("block_label_path").AsNodePath();
				var labelNode = GetNodeOrNull(labelPath);
				labelNode?.QueueFree();
			}
			oldNode.QueueFree();
			_structureNodes.Remove(gridPos);
		}
	}

	private int GetRoadMask(Vector2I p)
	{
		int mask = 0;
		if (IsRoadAt(new Vector2I(p.X, p.Y - 1))) mask |= RoadN;
		if (IsRoadAt(new Vector2I(p.X + 1, p.Y))) mask |= RoadE;
		if (IsRoadAt(new Vector2I(p.X, p.Y + 1))) mask |= RoadS;
		if (IsRoadAt(new Vector2I(p.X - 1, p.Y))) mask |= RoadW;
		return mask;
	}

	private bool IsRoadAt(Vector2I p) =>
		_structures.TryGetValue(p, out var t) && t == BuildTool.Road;

	private static string MaskToRoadKey(int mask)
	{
		var sb = new StringBuilder(4);
		if ((mask & RoadN) != 0) sb.Append('N');
		if ((mask & RoadE) != 0) sb.Append('E');
		if ((mask & RoadS) != 0) sb.Append('S');
		if ((mask & RoadW) != 0) sb.Append('W');
		return sb.ToString();
	}

	private bool TryGetRoadTexture(int mask, out Texture2D texture, out float rotation)
	{
		texture = null;
		rotation = 0f;
		string key = MaskToRoadKey(mask);
		if (_roadTextures.TryGetValue(key, out texture)) return true;
		if ((key == "NS" || key == "N" || key == "S") && _roadTextures.TryGetValue("EW", out texture))
		{
			rotation = Mathf.Pi * 0.5f;
			return true;
		}
		if ((key == "E" || key == "W") && _roadTextures.TryGetValue("EW", out texture)) return true;
		return false;
	}

	private Control CreateConnectedRoadFallback(Vector2I gridPos, int mask)
	{
		float size = TileSpacing;
		float thickness = Mathf.Max(10f, Mathf.Round(size * 0.36f));
		float center = (size - thickness) * 0.5f;

		var root = new Control
		{
			Name = $"Structure_{gridPos.X}_{gridPos.Y}",
			Position = new Vector2(gridPos.X * TileSpacing, gridPos.Y * TileSpacing),
			Size = new Vector2(size, size),
			ZIndex = 2,
			ClipContents = true,
			MouseFilter = Control.MouseFilterEnum.Ignore
		};

		AddRoadPart(root, new Rect2(center, center, thickness, thickness));
		if ((mask & RoadN) != 0) AddRoadPart(root, new Rect2(center, 0, thickness, center + 1f));
		if ((mask & RoadE) != 0) AddRoadPart(root, new Rect2(center + thickness - 1f, center, size - (center + thickness - 1f), thickness));
		if ((mask & RoadS) != 0) AddRoadPart(root, new Rect2(center, center + thickness - 1f, thickness, size - (center + thickness - 1f)));
		if ((mask & RoadW) != 0) AddRoadPart(root, new Rect2(0, center, center + 1f, thickness));
		return root;
	}

	private void AddRoadPart(Control parent, Rect2 rect)
	{
		if (_roadTileTexture != null)
		{
			parent.AddChild(new TextureRect
			{
				Position = rect.Position,
				Size = rect.Size,
				Texture = _roadTileTexture,
				StretchMode = TextureRect.StretchModeEnum.Scale,
				MouseFilter = Control.MouseFilterEnum.Ignore
			});
			return;
		}
		parent.AddChild(new ColorRect
		{
			Position = rect.Position,
			Size = rect.Size,
			Color = new Color(0.3f, 0.3f, 0.3f, 0.95f),
			MouseFilter = Control.MouseFilterEnum.Ignore
		});
	}

	// -------------------------------------------------------------------------
	// Route visualization
	// -------------------------------------------------------------------------
	private void DrawRouteLines(Dictionary<string, string> preferredFacility)
	{
		// Clear old lines
		foreach (var line in _routeLines)
			line.QueueFree();
		_routeLines.Clear();

		if (preferredFacility == null) return;

		for (int i = 0; i < _housePositions.Count; i++)
		{
			string houseId = $"House_{i}";
			if (!preferredFacility.TryGetValue(houseId, out string facilityId)) continue;
			if (string.IsNullOrEmpty(facilityId) || facilityId == "Disconnected") continue;

			Vector2I houseGrid = _housePositions[i];
			Vector2I facilityGrid;

			if (facilityId == "Clinic")
			{
				facilityGrid = _clinicPos;
			}
			else if (facilityId.StartsWith("Hospital_"))
			{
				if (!int.TryParse(facilityId.Replace("Hospital_", ""), out int hIdx)) continue;
				if (hIdx < 0 || hIdx >= _hospitalPositions.Count) continue;
				facilityGrid = _hospitalPositions[hIdx];
			}
			else
			{
				continue; // unknown facility
			}

			bool toClinic = facilityId == "Clinic";

			var line = new Line2D
			{
				Width = 3f,
				DefaultColor = toClinic
					? new Color(0.2f, 0.9f, 0.3f, 0.7f)     // green → clinic
					: new Color(0.9f, 0.2f, 0.2f, 0.55f),    // red → hospital
				ZIndex = 3,
				Name = $"RouteLine_{i}",
				Antialiased = true
			};

			Vector2 from = TileCenter(houseGrid);
			Vector2 to = TileCenter(facilityGrid);
			line.AddPoint(from);
			line.AddPoint(to);
			AddChild(line);
			_routeLines.Add(line);
		}
	}

	private Vector2 TileCenter(Vector2I gridPos)
	{
		return new Vector2(
			gridPos.X * TileSpacing + TileSpacing * 0.5f,
			gridPos.Y * TileSpacing + TileSpacing * 0.5f
		);
	}

	// -------------------------------------------------------------------------
	// HUD creation
	// -------------------------------------------------------------------------
	private PanelContainer CreateStyledPanel(Color bgColor, Vector2 minSize)
	{
		var panel = new PanelContainer { CustomMinimumSize = minSize };
		var style = new StyleBoxFlat
		{
			BgColor = bgColor,
			CornerRadiusTopLeft = 12,
			CornerRadiusTopRight = 12,
			CornerRadiusBottomLeft = 12,
			CornerRadiusBottomRight = 12,
			ContentMarginLeft = 16,
			ContentMarginRight = 16,
			ContentMarginTop = 16,
			ContentMarginBottom = 16,
			BorderWidthLeft = 2,
			BorderWidthTop = 2,
			BorderWidthRight = 2,
			BorderWidthBottom = 2,
			BorderColor = new Color(1f, 1f, 1f, 0.15f),
			ShadowColor = new Color(0, 0, 0, 0.3f),
			ShadowSize = 8
		};
		panel.AddThemeStyleboxOverride("panel", style);
		return panel;
	}

	private void CreateHud()
	{
		var layer = new CanvasLayer { Name = "HUDLayer" };
		AddChild(layer);

		// --- Player Stats (Top Left) ---
		_playerStatsPanel = CreateStyledPanel(new Color(0.12f, 0.35f, 0.2f, 0.9f), new Vector2(260, 90));
		layer.AddChild(_playerStatsPanel);
		var playerVBox = new VBoxContainer();
		_playerStatsPanel.AddChild(playerVBox);
		
		_playerBudgetLabel = new Label { Text = "💰 $0.0" };
		_playerBudgetLabel.AddThemeFontSizeOverride("font_size", 36);
		_playerBudgetLabel.AddThemeColorOverride("font_color", new Color(0.5f, 1.0f, 0.6f));
		playerVBox.AddChild(_playerBudgetLabel);
		
		_previousProfitLabel = new Label { Text = "📈 +$0.0" };
		_previousProfitLabel.AddThemeFontSizeOverride("font_size", 18);
		_previousProfitLabel.AddThemeColorOverride("font_color", new Color(0.8f, 1.0f, 0.8f));
		playerVBox.AddChild(_previousProfitLabel);

		// --- City Stats (Top Center) ---
		_cityStatsPanel = CreateStyledPanel(new Color(0.4f, 0.12f, 0.15f, 0.9f), new Vector2(220, 80));
		layer.AddChild(_cityStatsPanel);
		var cityVBox = new VBoxContainer();
		_cityStatsPanel.AddChild(cityVBox);
		
		var cityTitle = MakeLabel("🏙 City Budget", bold: true);
		cityTitle.HorizontalAlignment = HorizontalAlignment.Center;
		cityVBox.AddChild(cityTitle);
		
		_optimizerBudgetLabel = new Label { Text = "$0.0", HorizontalAlignment = HorizontalAlignment.Center };
		_optimizerBudgetLabel.AddThemeFontSizeOverride("font_size", 28);
		_optimizerBudgetLabel.AddThemeColorOverride("font_color", new Color(1.0f, 0.5f, 0.5f));
		cityVBox.AddChild(_optimizerBudgetLabel);

		// --- Clinic Stats (Top Right) ---
		_clinicStatsPanel = CreateStyledPanel(new Color(0.15f, 0.2f, 0.35f, 0.9f), new Vector2(320, 110));
		layer.AddChild(_clinicStatsPanel);
		var clinicVBox = new VBoxContainer();
		_clinicStatsPanel.AddChild(clinicVBox);

		var topRow = new HBoxContainer();
		_dayLabel = new Label { Text = "📅 Turn 0" };
		_dayLabel.AddThemeFontSizeOverride("font_size", 20);
		topRow.AddChild(_dayLabel);
		
		topRow.AddChild(new Control { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill }); // Spacer
		
		_coverageLabel = new Label { Text = "Coverage" };
		_coverageLabel.AddThemeFontSizeOverride("font_size", 20);
		topRow.AddChild(_coverageLabel);
		clinicVBox.AddChild(topRow);

		// Health bar for clinic money
		var healthBarContainer = new VBoxContainer();
		var clinicHealthTitle = MakeLabel("Clinic Health ($)", bold: true);
		clinicHealthTitle.HorizontalAlignment = HorizontalAlignment.Center;
		healthBarContainer.AddChild(clinicHealthTitle);
		
		_clinicHealthBarBg = new ColorRect
		{
			CustomMinimumSize = new Vector2(280, 20),
			Color = new Color(0.1f, 0.1f, 0.15f)
		};
		_clinicHealthBar = new ColorRect
		{
			CustomMinimumSize = new Vector2(280, 20),
			Color = new Color(0.2f, 0.8f, 0.3f),
			Size = new Vector2(280, 20),
			Position = Vector2.Zero
		};
		var barStack = new Control { CustomMinimumSize = new Vector2(280, 20) };
		barStack.AddChild(_clinicHealthBarBg);
		barStack.AddChild(_clinicHealthBar);
		healthBarContainer.AddChild(barStack);
		clinicVBox.AddChild(healthBarContainer);

		// --- Actions panel (bottom-left) ---
		_actionsPanel = CreateStyledPanel(new Color(0.12f, 0.12f, 0.15f, 0.95f), new Vector2(370, 320));
		layer.AddChild(_actionsPanel);

		var actionsRoot = new VBoxContainer();
		_actionsPanel.AddChild(actionsRoot);

		actionsRoot.AddChild(MakeLabel("🛠  Road Management", bold: true));
		_roadBuildButton = MakeButton($"🔨 Build Road (${RoadCost:0})");
		_roadDestroyButton = MakeButton($"🗑 Destroy Road (${RoadDestroyCost:0})");
		_roadUpgradeButton = MakeButton($"⬆ Upgrade Road (${UpgradeCost:0})");
		_roadBuildButton.Pressed += () => SetSelectedTool(BuildTool.Road);
		_roadDestroyButton.Pressed += () => SetSelectedTool(BuildTool.RoadDestroy);
		_roadUpgradeButton.Pressed += () => SetSelectedTool(BuildTool.RoadUpgrade);
		actionsRoot.AddChild(_roadBuildButton);
		actionsRoot.AddChild(_roadDestroyButton);
		actionsRoot.AddChild(_roadUpgradeButton);

		actionsRoot.AddChild(MakeLabel("🏪  Infrastructure", bold: true));
		_mallBuildButton = MakeButton($"🛍 Build Mall (${MallCost:0}) [adds congestion]");
		_mallDestroyButton = MakeButton("🗑 Destroy Mall");
		_mallBuildButton.Pressed += () => SetSelectedTool(BuildTool.Mall);
		_mallDestroyButton.Pressed += () => SetSelectedTool(BuildTool.MallDestroy);
		actionsRoot.AddChild(_mallBuildButton);
		actionsRoot.AddChild(_mallDestroyButton);

		actionsRoot.AddChild(MakeLabel("⚡  Special Powers", bold: true));
		_cutHospitalFundingButton = MakeButton($"✂ Cut Hospital Funding (${CutFundingCost:0})");
		_roadBlockageButton = MakeButton($"🚧 Road Blockage (${BlockageCost:0})");
		_cutHospitalFundingButton.Pressed += TryCutHospitalFunding;
		_roadBlockageButton.Pressed += () => SetSelectedTool(BuildTool.RoadBlockage);
		actionsRoot.AddChild(_cutHospitalFundingButton);
		actionsRoot.AddChild(_roadBlockageButton);

		_toolLabel = new Label { Text = "Selected: Pointer" };
		actionsRoot.AddChild(_toolLabel);

		_statusLabel = new Label { CustomMinimumSize = new Vector2(370, 56) };
		_statusLabel.AutowrapMode = TextServer.AutowrapMode.WordSmart;
		actionsRoot.AddChild(_statusLabel);

		// Floating tooltip in world space (child of scene, not HUD layer)
		_hoverTooltip = new PanelContainer
		{
			Visible = false,
			ZIndex = 50,
			MouseFilter = Control.MouseFilterEnum.Ignore,
		};
		_hoverTooltipLabel = new Label
		{
			CustomMinimumSize = new Vector2(240, 0),
			AutowrapMode = TextServer.AutowrapMode.WordSmart,
		};
		_hoverTooltipLabel.AddThemeFontSizeOverride("font_size", 20);
		_hoverTooltip.AddChild(_hoverTooltipLabel);
		AddChild(_hoverTooltip); // world-space child, NOT on CanvasLayer

		_endTurnButton = MakeButton("⏭  End Turn");
		_endTurnButton.Pressed += EndTurn;
		actionsRoot.AddChild(_endTurnButton);

		// Pointer button (hidden, used for deselect)
		_pointerButton = new Button { Visible = false, ToggleMode = true };
		_pointerButton.Pressed += () => SetSelectedTool(BuildTool.None);
		layer.AddChild(_pointerButton);

		UpdateHudLayout();
	}

	private static Label MakeLabel(string text, bool bold = false)
	{
		var lbl = new Label { Text = text };
		if (bold) lbl.AddThemeFontSizeOverride("font_size", 14);
		return lbl;
	}

	private static Button MakeButton(string text)
	{
		return new Button
		{
			Text = text,
			ToggleMode = true,
			CustomMinimumSize = new Vector2(0, 28)
		};
	}

	private void UpdateHudLayout()
	{
		if (_playerStatsPanel == null || _actionsPanel == null) return;

		float vw = GetViewportRect().Size.X;
		float vh = GetViewportRect().Size.Y;

		// Top Left: Player Stats
		_playerStatsPanel.Position = new Vector2(16f, 16f);

		// Top Center: City Stats
		float cityW = _cityStatsPanel.Size.X > 0f ? _cityStatsPanel.Size.X : _cityStatsPanel.CustomMinimumSize.X;
		_cityStatsPanel.Position = new Vector2((vw - cityW) / 2f, 16f);

		// Top Right: Clinic Stats
		float clinicW = _clinicStatsPanel.Size.X > 0f ? _clinicStatsPanel.Size.X : _clinicStatsPanel.CustomMinimumSize.X;
		_clinicStatsPanel.Position = new Vector2(vw - clinicW - 16f, 16f);

		// Bottom Left: Actions Panel
		float actH = _actionsPanel.Size.Y > 0f ? _actionsPanel.Size.Y : _actionsPanel.CustomMinimumSize.Y;
		_actionsPanel.Position = new Vector2(16f, vh - actH - 16f);
	}

	private void SetSelectedTool(BuildTool tool)
	{
		_selectedTool = tool;
		_roadBuildButton.ButtonPressed = tool == BuildTool.Road;
		_roadDestroyButton.ButtonPressed = tool == BuildTool.RoadDestroy;
		_roadUpgradeButton.ButtonPressed = tool == BuildTool.RoadUpgrade;
		_mallBuildButton.ButtonPressed = tool == BuildTool.Mall;
		_mallDestroyButton.ButtonPressed = tool == BuildTool.MallDestroy;
		_roadBlockageButton.ButtonPressed = tool == BuildTool.RoadBlockage;
		_toolLabel.Text = $"Selected: {ToolName(tool)}";
	}

	private static string ToolName(BuildTool tool) => tool switch
	{
		BuildTool.Road => "Build Road",
		BuildTool.RoadDestroy => "Destroy Road",
		BuildTool.RoadUpgrade => "Upgrade Road",
		BuildTool.Mall => "Build Mall",
		BuildTool.MallDestroy => "Destroy Mall",
		BuildTool.RoadBlockage => "Road Blockage",
		_ => "Pointer"
	};

	private void UpdateHud()
	{
		if (_dayLabel == null) return;
		_dayLabel.Text = $"📅 Turn {_day}";
		_playerBudgetLabel.Text = $"💰 ${_playerBudget:0.0}";
		_optimizerBudgetLabel.Text = $"${_citizenBudget:0.0}";
		
		string profitSign = _previousDayProfit >= 0 ? "+" : "";
		_previousProfitLabel.Text = $"📈 {profitSign}${_previousDayProfit:0.0}";
		_previousProfitLabel.AddThemeColorOverride("font_color", _previousDayProfit >= 0 ? new Color(0.6f, 1.0f, 0.6f) : new Color(1.0f, 0.6f, 0.6f));

		// Coverage label — color by health
		if (_coverageLabel != null)
		{
			// Will be set after receiving response
		}

		if (_clinicHealthBar != null && _clinicHealthBarBg != null)
		{
			float maxMoney = 120f;
			float ratio = Mathf.Clamp(_clinicMoney / maxMoney, 0f, 1f);
			_clinicHealthBar.Size = new Vector2(280f * ratio, 20f);
			_clinicHealthBar.Color = ratio > 0.5f
				? new Color(0.2f, 0.8f, 0.3f)
				: ratio > 0.25f
					? new Color(0.9f, 0.7f, 0.1f)
					: new Color(0.9f, 0.2f, 0.1f);
		}

		// Enable/disable cut funding button
		if (_cutHospitalFundingButton != null)
		{
			_cutHospitalFundingButton.Disabled = _fundingCutUsedThisTurn || _gameOver;
			_cutHospitalFundingButton.Text = _fundingCutUsedThisTurn
				? "✂ Funding Cut (used this turn)"
				: $"✂ Cut Hospital Funding (${CutFundingCost:0})";
		}
	}

	private void UpdateCoverageLabel(int clinicCount, float clinicRatio)
	{
		if (_coverageLabel == null) return;
		int total = _housePositions.Count;
		_coverageLabel.Text = $"🏥 Clinic: {clinicCount}/{total} houses ({clinicRatio * 100f:0.0}%)";
		// Color is purely informational — coverage doesn’t end the game, money does
		if (clinicRatio >= 0.5f)
			_coverageLabel.Modulate = new Color(0.3f, 1f, 0.4f);
		else if (clinicRatio >= 0.25f)
			_coverageLabel.Modulate = new Color(1f, 0.9f, 0.2f);
		else
			_coverageLabel.Modulate = new Color(1f, 0.3f, 0.3f);
	}

	// -------------------------------------------------------------------------
	// Player actions
	// -------------------------------------------------------------------------
	private float GetToolCost(BuildTool tool) => tool switch
	{
		BuildTool.Road => RoadCost,
		BuildTool.RoadDestroy => RoadDestroyCost,
		BuildTool.Mall => MallCost,
		BuildTool.RoadUpgrade => UpgradeCost,
		BuildTool.RoadBlockage => BlockageCost,
		_ => 0f
	};

	private void TryPlaceSelectedToolAtMouse()
	{
		if (_gameOver || _turnInProgress) return;
		if (_selectedTool == BuildTool.None) return;
		if (GetViewport().GuiGetHoveredControl() != null) return;

		Vector2 mouseWorld = GetGlobalMousePosition();
		Vector2I gridPos = new(
			Mathf.FloorToInt(mouseWorld.X / TileSpacing),
			Mathf.FloorToInt(mouseWorld.Y / TileSpacing)
		);

		if (gridPos.X < 0 || gridPos.X >= GridSize || gridPos.Y < 0 || gridPos.Y >= GridSize)
			return;

		switch (_selectedTool)
		{
			case BuildTool.Road:
				TryBuildRoad(gridPos);
				break;
			case BuildTool.RoadDestroy:
				TryDestroyRoad(gridPos);
				break;
			case BuildTool.RoadUpgrade:
				TryUpgradeRoad(gridPos);
				break;
			case BuildTool.Mall:
				TryBuildMall(gridPos);
				break;
			case BuildTool.MallDestroy:
				TryDestroyMall(gridPos);
				break;
			case BuildTool.RoadBlockage:
				TryBlockRoad(gridPos);
				break;
		}
	}

	private void TryBuildRoad(Vector2I gridPos)
	{
		if (_occupiedTiles.Contains(gridPos)) { _statusLabel.Text = "Cannot build on clinic/hospital/house."; return; }
		if (_structures.ContainsKey(gridPos)) { _statusLabel.Text = "Tile already has a structure."; return; }
		if (_playerBudget < RoadCost) { _statusLabel.Text = "Not enough budget."; return; }

		_playerBudget -= RoadCost;
		PlaceStructure(gridPos, BuildTool.Road, animated: true);
		UpdateHud();
		_statusLabel.Text = $"Road built at ({gridPos.X}, {gridPos.Y}).";
	}

	private void TryDestroyRoad(Vector2I gridPos)
	{
		if (!_structures.TryGetValue(gridPos, out var t) || t != BuildTool.Road)
		{
			_statusLabel.Text = "No road to destroy here.";
			return;
		}
		if (_playerBudget < RoadDestroyCost) { _statusLabel.Text = "Not enough budget to demolish."; return; }
		_playerBudget -= RoadDestroyCost;
		DestroyStructureAt(gridPos, animated: true);
		UpdateHud();
		_statusLabel.Text = $"Road demolished at ({gridPos.X}, {gridPos.Y}). Cost: ${RoadDestroyCost:0.0}";
	}

	private void TryUpgradeRoad(Vector2I gridPos)
	{
		if (!_structures.TryGetValue(gridPos, out var t) || t != BuildTool.Road)
		{
			_statusLabel.Text = "No road to upgrade here.";
			return;
		}
		if (_upgradedRoads.Contains(gridPos)) { _statusLabel.Text = "Road already upgraded."; return; }
		if (_playerBudget < UpgradeCost) { _statusLabel.Text = "Not enough budget."; return; }

		_playerBudget -= UpgradeCost;
		_upgradedRoads.Add(gridPos);
		RefreshRoadAt(gridPos); // re-render with green tint
		if (_structureNodes.TryGetValue(gridPos, out var node))
			AnimateUpgrade(node);
		UpdateHud();
		_statusLabel.Text = $"Road upgraded at ({gridPos.X}, {gridPos.Y}). Travel cost reduced.";
	}

	private void TryBuildMall(Vector2I gridPos)
	{
		if (_occupiedTiles.Contains(gridPos)) { _statusLabel.Text = "Cannot build on clinic/hospital/house."; return; }
		if (_structures.ContainsKey(gridPos)) { _statusLabel.Text = "Tile already has a structure."; return; }
		if (_playerBudget < MallCost) { _statusLabel.Text = "Not enough budget."; return; }

		_playerBudget -= MallCost;
		PlaceStructure(gridPos, BuildTool.Mall, animated: true);
		UpdateHud();
		_statusLabel.Text = $"Mall built at ({gridPos.X}, {gridPos.Y}). Adds congestion to nearby roads.";
	}

	private void TryDestroyMall(Vector2I gridPos)
	{
		if (!_structures.TryGetValue(gridPos, out var t) || t != BuildTool.Mall)
		{
			_statusLabel.Text = "No mall to destroy here.";
			return;
		}
		DestroyStructureAt(gridPos, animated: true);
		UpdateHud();
		_statusLabel.Text = $"Mall demolished at ({gridPos.X}, {gridPos.Y}).";
	}

	private void TryBlockRoad(Vector2I gridPos)
	{
		if (!_structures.TryGetValue(gridPos, out var t) || t != BuildTool.Road)
		{
			_statusLabel.Text = "No road to block here.";
			return;
		}
		if (_blockedRoads.Contains(gridPos)) { _statusLabel.Text = "Road already blocked this turn."; return; }
		if (_playerBudget < BlockageCost) { _statusLabel.Text = "Not enough budget."; return; }

		_playerBudget -= BlockageCost;
		_blockedRoads.Add(gridPos);
		RefreshRoadAt(gridPos); // orange tint
		if (_structureNodes.TryGetValue(gridPos, out var node))
			AnimateBlock(node);
		UpdateHud();
		_statusLabel.Text = $"🚧 Road blocked at ({gridPos.X}, {gridPos.Y}) for this turn.";
	}

	private void TryCutHospitalFunding()
	{
		if (_fundingCutUsedThisTurn) { _statusLabel.Text = "Already cut funding this turn."; return; }
		if (_playerBudget < CutFundingCost) { _statusLabel.Text = "Not enough budget."; return; }

		_playerBudget -= CutFundingCost;
		_hospitalFundingCutActive = true;
		_fundingCutUsedThisTurn = true;
		UpdateHud();
		_statusLabel.Text = "✂ Hospital funding cut! All hospital routes cost more this turn.";
	}

	// -------------------------------------------------------------------------
	// Turn logic
	// -------------------------------------------------------------------------
	private void SendInitialOptimizationRequest()
	{
		_statusLabel.Text = "Initial handoff to GAMSPy...";
		_resolveTurnAfterResponse = false;
		SendOptimizationRequest();
	}

	private void EndTurn()
	{
		if (_gameOver || _turnInProgress) return;
		if (_budgetLossPending)
		{
			_gameOver = true;
			DisableAllButtons();
			ShowGameOverScreen("The clinic stayed in debt for a full turn.");
			return;
		}
		_turnInProgress = true;
		_endTurnButton.Disabled = true;
		_resolveTurnAfterResponse = true;
		_statusLabel.Text = "Turn ended. Contacting GAMSPy optimizer...";
		SendOptimizationRequest();
	}

	private void SendOptimizationRequest()
	{
		var payload = BuildOptimizationPayload();
		string json = JsonSerializer.Serialize(payload);
		string[] headers = { "Content-Type: application/json" };

		Error err = _http.Request("http://127.0.0.1:8000/solve", headers, HttpClient.Method.Post, json);
		if (err != Error.Ok)
		{
			GD.PrintErr($"HTTP request error: {err}");
			_statusLabel.Text = $"HTTP error: {err}";
			_turnInProgress = false;
			_endTurnButton.Disabled = _gameOver;
		}
		else
		{
			GD.Print("Sent optimization request.");
		}
	}

	private SolveRequest BuildOptimizationPayload()
	{
		var facilities = new List<FacilityData>
		{
			new() { id = "Clinic", type = "clinic", x = _clinicPos.X, y = _clinicPos.Y }
		};
		for (int i = 0; i < _hospitalPositions.Count; i++)
		{
			var hp = _hospitalPositions[i];
			facilities.Add(new() { id = $"Hospital_{i}", type = "hospital", x = hp.X, y = hp.Y });
		}

		var houses = new List<HouseData>();
		for (int i = 0; i < _housePositions.Count; i++)
		{
			var hp = _housePositions[i];
			houses.Add(new() { id = $"House_{i}", x = hp.X, y = hp.Y });
		}

		var existingRoads = new List<GridPoint>();
		var mallPositions = new List<GridPoint>();
		var upgradedRoads = new List<GridPoint>();
		var blockedRoads = new List<GridPoint>();

		foreach (var kv in _structures)
		{
			if (kv.Value == BuildTool.Road)
				existingRoads.Add(new() { x = kv.Key.X, y = kv.Key.Y });
			else if (kv.Value == BuildTool.Mall)
				mallPositions.Add(new() { x = kv.Key.X, y = kv.Key.Y });
		}
		foreach (var p in _upgradedRoads)
			upgradedRoads.Add(new() { x = p.X, y = p.Y });
		foreach (var p in _blockedRoads)
			blockedRoads.Add(new() { x = p.X, y = p.Y });

		return new SolveRequest
		{
			houses = houses,
			facilities = facilities,
			house_access = new List<FacilityCost>(), // computed server-side now
			existing_roads = existingRoads,
			budget = Mathf.Min(_playerBudget, _clinicMoney),
			road_cost = RoadCost,
			turn_index = _day,
			setup_phase = !_resolveTurnAfterResponse,
			mall_positions = mallPositions,
			upgraded_roads = upgradedRoads,
			blocked_roads = blockedRoads,
			hospital_funding_cut = _hospitalFundingCutActive,
			citizen_budget = _citizenBudget,
		};
	}

	// -------------------------------------------------------------------------
	// Optimizer response handling
	// -------------------------------------------------------------------------
	private void OnRequestCompleted(long result, long responseCode, string[] headers, byte[] body)
	{
		HideLoadingScreen();  // dismiss regardless of success/failure
		string responseText = Encoding.UTF8.GetString(body);
		GD.Print($"Optimizer response: {responseCode}");

		if (result != (long)HttpRequest.Result.Success || responseCode < 200 || responseCode >= 300)
		{
			string msg = $"Optimizer error {responseCode}: {responseText}";
			GD.PrintErr(msg);
			_statusLabel.Text = msg;
			_turnInProgress = false;
			_endTurnButton.Disabled = _gameOver;
			return;
		}

		if (string.IsNullOrWhiteSpace(responseText))
		{
			_statusLabel.Text = "Optimizer returned empty response.";
			_turnInProgress = false;
			_endTurnButton.Disabled = _gameOver;
			return;
		}

		SolveResponse response = null;
		try { response = JsonSerializer.Deserialize<SolveResponse>(responseText); }
		catch (Exception e)
		{
			_statusLabel.Text = $"Parse error: {e.Message}";
			_turnInProgress = false;
			_endTurnButton.Disabled = _gameOver;
			return;
		}

		// Apply all topology changes before trusting the solver's facility choices.
		int playerRoadsBuilt = ApplyRoadList(response?.built_roads, isOptimizerRoad: false, animate: _resolveTurnAfterResponse);
		int cityRoadsBuilt   = ApplyRoadList(response?.optimizer_roads, isOptimizerRoad: true, animate: _resolveTurnAfterResponse);
		int destroyedRoads   = ApplyDestroyedRoads(response?.destroyed_roads);

		var preferredFacility = SanitizePreferredFacilities(response?.preferred_facility);
		UpdateFacilityPatientCounts(preferredFacility);
		DrawRouteLines(preferredFacility);

		int clinicCount  = response?.clinic_count ?? 0;
		float clinicRatio = response?.clinic_ratio ?? 0f;
		UpdateCoverageLabel(clinicCount, clinicRatio);

		if (_resolveTurnAfterResponse)
		{
			float roadSpend = playerRoadsBuilt * RoadCost;
			ResolveTurn(response, roadSpend, playerRoadsBuilt, cityRoadsBuilt, destroyedRoads);
			_resolveTurnAfterResponse = false;
		}
		else
		{
			_statusLabel.Text = playerRoadsBuilt > 0
				? $"Initial setup done. GAMSPy placed {playerRoadsBuilt} road(s). Build and press End Turn."
				: "Ready. Build roads and press End Turn.";
		}

		_turnInProgress = false;
		_endTurnButton.Disabled = _gameOver;
		UpdateHud();
	}

	private void ResolveTurn(SolveResponse response, float roadSpend, int playerRoads, int cityRoads, int destroyedRoads)
	{
		_day++;

		int clinicCount   = response?.clinic_count ?? 0;
		float clinicRatio = response?.clinic_ratio ?? 0f;

		// Economy
		float income  = clinicCount * RevenuePerPatient;
		float upkeep  = DailyClinicUpkeep + Mathf.Min(_day * DailyUpkeepGrowth, 15f);
		float profit  = income - upkeep - roadSpend;

		_previousDayProfit = profit;
		_totalRevenue += income;
		_peakCoverage = Mathf.Max(_peakCoverage, clinicRatio);

		_playerBudget += income - upkeep;
		_playerBudget -= roadSpend;
		_clinicMoney  += income - upkeep;
		_clinicMoney  -= roadSpend;
		_budgetLossPending = _playerBudget < 0f;

		// City counter-turn budget
		_citizenBudget += 3f + (_housePositions.Count * 0.3f);

		// Reset per-turn effects
		_blockedRoads.Clear();
		_hospitalFundingCutActive = false;
		_fundingCutUsedThisTurn = false;

		// Re-render roads (unblock tints)
		foreach (var kv in _structures)
			if (kv.Value == BuildTool.Road)
				RefreshRoadAt(kv.Key);

		if (CheckLoseCondition(clinicRatio)) return;
		_statusLabel.Text = _budgetLossPending
			? $"Day {_day}: {clinicCount}/{_housePositions.Count} chose clinic ({clinicRatio * 100f:0.0}%). Budget is below $0. End Turn will trigger game over."
			: $"Day {_day}: {clinicCount}/{_housePositions.Count} chose clinic ({clinicRatio * 100f:0.0}%). " +
			  $"Income: ${income:0.0} | Upkeep: ${upkeep:0.0} | City built {cityRoads}, destroyed {destroyedRoads} road(s).";
	}

	private int ApplyRoadList(List<GridPoint> roads, bool isOptimizerRoad, bool animate)
	{
		if (roads == null) return 0;
		int applied = 0;
		foreach (var road in roads)
		{
			Vector2I pos = new(road.x, road.y);
			if (!IsBuildableRoadTile(pos)) continue;
			PlaceStructure(pos, BuildTool.Road, animated: animate, isOptimizerRoad: isOptimizerRoad);
			applied++;
		}
		return applied;
	}

	private int ApplyDestroyedRoads(List<GridPoint> roads)
	{
		if (roads == null) return 0;
		int count = 0;
		foreach (var road in roads)
		{
			Vector2I pos = new(road.x, road.y);
			if (!_structures.TryGetValue(pos, out var t) || t != BuildTool.Road) continue;
			DestroyStructureAt(pos, animated: true);
			count++;
		}
		return count;
	}

	private void UpdateFacilityPatientCounts(Dictionary<string, string> preferredFacility)
	{
		_facilityPatientCounts.Clear();
		if (preferredFacility == null) return;
		foreach (var kv in preferredFacility)
		{
			string fid = kv.Value;
			if (string.IsNullOrEmpty(fid) || fid == "Disconnected") continue;
			_facilityPatientCounts[fid] = _facilityPatientCounts.TryGetValue(fid, out int cur) ? cur + 1 : 1;
		}
	}

	// -------------------------------------------------------------------------
	// Lose / game-over
	// -------------------------------------------------------------------------
	private bool CheckLoseCondition(float clinicRatio = -1f)
	{
		if (_clinicMoney > 0f)
			return false;

		_gameOver = true;
		DisableAllButtons();
		ShowGameOverScreen("The clinic ran out of money.");
		return true;
	}

	private void DisableAllButtons()
	{
		foreach (Button btn in new[] {
			_endTurnButton, _pointerButton, _roadBuildButton, _roadDestroyButton,
			_roadUpgradeButton, _mallBuildButton, _mallDestroyButton,
			_cutHospitalFundingButton, _roadBlockageButton
		})
		{
			if (btn != null) btn.Disabled = true;
		}
	}

	private void ShowGameOverScreen(string reason)
	{
		var layer = GetNode<CanvasLayer>("HUDLayer");

		// Dark overlay
		var overlay = new ColorRect
		{
			Color = new Color(0f, 0f, 0f, 0.72f),
			AnchorRight = 1f,
			AnchorBottom = 1f,
			ZIndex = 100,
			MouseFilter = Control.MouseFilterEnum.Ignore
		};
		layer.AddChild(overlay);

		// Panel in center
		var panel = new PanelContainer
		{
			CustomMinimumSize = new Vector2(420, 250),
			ZIndex = 101
		};
		layer.AddChild(panel);

		var vbox = new VBoxContainer();
		panel.AddChild(vbox);

		var titleLbl = new Label
		{
			Text = "🏚  CLINIC CLOSED",
			HorizontalAlignment = HorizontalAlignment.Center
		};
		titleLbl.AddThemeFontSizeOverride("font_size", 26);
		titleLbl.Modulate = new Color(1f, 0.3f, 0.3f);
		vbox.AddChild(titleLbl);

		vbox.AddChild(new Label { Text = reason, HorizontalAlignment = HorizontalAlignment.Center });
		vbox.AddChild(new Label { Text = $"Days survived: {_day}", HorizontalAlignment = HorizontalAlignment.Center });
		vbox.AddChild(new Label { Text = $"Peak clinic coverage: {_peakCoverage * 100f:0.0}%", HorizontalAlignment = HorizontalAlignment.Center });
		vbox.AddChild(new Label { Text = $"Total revenue earned: ${_totalRevenue:0.0}", HorizontalAlignment = HorizontalAlignment.Center });

		// Center panel after one frame
		CallDeferred(nameof(CenterNode), panel);
	}

	private void CenterNode(PanelContainer panel)
	{
		var vs = GetViewportRect().Size;
		panel.Position = (vs - panel.Size) * 0.5f;
	}

	// -------------------------------------------------------------------------
	// Utilities
	// -------------------------------------------------------------------------
	private bool IsBuildableRoadTile(Vector2I pos)
	{
		if (pos.X < 0 || pos.X >= GridSize || pos.Y < 0 || pos.Y >= GridSize) return false;
		if (_occupiedTiles.Contains(pos)) return false;
		return !_structures.ContainsKey(pos);
	}

	private static int Manhattan(Vector2I a, Vector2I b) =>
		Mathf.Abs(a.X - b.X) + Mathf.Abs(a.Y - b.Y);

	private float Distance(Vector2I a, Vector2I b) =>
		Mathf.Sqrt(Mathf.Pow(a.X - b.X, 2) + Mathf.Pow(a.Y - b.Y, 2));

	public override void _UnhandledInput(InputEvent @event)
	{
		if (@event is InputEventMouseButton mb)
		{
			if (mb.ButtonIndex == MouseButton.WheelUp && mb.Pressed)
				_camera.Zoom *= 1.1f;
			if (mb.ButtonIndex == MouseButton.WheelDown && mb.Pressed)
				_camera.Zoom *= 0.9f;
			if (mb.ButtonIndex == MouseButton.Middle)
				_isDragging = mb.Pressed;
			if (mb.ButtonIndex == MouseButton.Left && mb.Pressed)
				TryPlaceSelectedToolAtMouse();
			if (mb.ButtonIndex == MouseButton.Right && mb.Pressed)
				SetSelectedTool(BuildTool.None);
		}
		else if (@event is InputEventMouseMotion mm && _isDragging)
		{
			_camera.Position -= mm.Relative / _camera.Zoom;
		}
	}
}

// ---------------------------------------------------------------------------
// Request / response models
// ---------------------------------------------------------------------------

public class SolveRequest
{
	public List<HouseData> houses { get; set; }
	public List<FacilityData> facilities { get; set; }
	public List<FacilityCost> house_access { get; set; }
	public float budget { get; set; }
	public List<GridPoint> existing_roads { get; set; }
	public float road_cost { get; set; }
	public int turn_index { get; set; }
	public bool setup_phase { get; set; }
	// New fields
	public List<GridPoint> mall_positions { get; set; }
	public List<GridPoint> upgraded_roads { get; set; }
	public List<GridPoint> blocked_roads { get; set; }
	public bool hospital_funding_cut { get; set; }
	public float citizen_budget { get; set; }
}

public class HouseData
{
	public string id { get; set; }
	public int x { get; set; }
	public int y { get; set; }
}

public class FacilityData
{
	public string id { get; set; }
	public string type { get; set; }
	public int x { get; set; }
	public int y { get; set; }
}

public class FacilityCost
{
	public string house { get; set; }
	public string facility { get; set; }
	public float travel_cost { get; set; }
}

public class GridPoint
{
	public int x { get; set; }
	public int y { get; set; }
}

public class SolveResponse
{
	public Dictionary<string, string> preferred_facility { get; set; }
	public int clinic_count { get; set; }
	public float clinic_ratio { get; set; }
	public bool clinic_alive { get; set; }
	public float citizen_cost_objective { get; set; }
	public string solver_status { get; set; }
	public float budget_seen { get; set; }
	public float spent_budget { get; set; }
	public List<GridPoint> built_roads { get; set; }
	public List<GridPoint> optimizer_roads { get; set; }
	public List<GridPoint> destroyed_roads { get; set; }  // city-destroyed roads (after turn 5)
}
