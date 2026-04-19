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

	private const int GridSize = 20;
	private const int TileSpacing = 64;

	// Road neighbor bit flags (N/E/S/W)
	private const int RoadN = 1;
	private const int RoadE = 2;
	private const int RoadS = 4;
	private const int RoadW = 8;

	private const int HospitalCount = 4;
	private const int HouseCount = 4; // must be < hospitals + clinic

	private const float RoadCost = 3f;
	private const float MallCost = 5f;

	private const float RevenuePerPatient = 10f;
	private const float DailyClinicUpkeep = 8f;
	private const float DailyUpkeepGrowth = 0.6f;

	private readonly HashSet<Vector2I> _occupiedTiles = new();
	private readonly List<Vector2I> _hospitalPositions = new();
	private readonly List<Vector2I> _housePositions = new();
	private readonly Dictionary<Vector2I, BuildTool> _structures = new();
	private readonly Dictionary<Vector2I, Control> _structureNodes = new();

	private Vector2I _clinicPos;

	// Game state
	private int _day = 0;
	private float _playerBudget = 40f;
	private float _citizenBudget = 30f;
	private float _clinicMoney = 40f;
	private float _previousDayProfit = 0f;

	private float _citizenHospitalBonus = 0f; // lowers hospital cost over time
	private float _citizenClinicPenalty = 0f; // raises clinic cost over time

	private bool _turnInProgress = false;
	private bool _resolveTurnAfterResponse = false;
	private bool _gameOver = false;

	private Vector2I _lastHoveredTile = new(-1, -1);
	private float _hoverTimer = 0f;

	// HUD
	private Label _dayLabel;
	private Label _playerBudgetLabel;
	private Label _optimizerBudgetLabel;
	private Label _previousProfitLabel;
	private Label _toolLabel;
	private Label _statusLabel;
	private Label _hoverLabel;
	private PanelContainer _infoPanel;
	private PanelContainer _actionsPanel;

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
		Mall
	}

	public override void _Ready()
	{
		_camera = GetNode<Camera2D>("Camera2D");
		_camera.MakeCurrent();

		LoadRoadTextures();

		_http = new HttpRequest();
		AddChild(_http);
		_http.RequestCompleted += OnRequestCompleted;

		// Ocean background outside island/map.
		RenderingServer.SetDefaultClearColor(new Color(0.12f, 0.45f, 0.85f));

		GenerateGrid();
		CreateHud();
		UpdateHud();
		SetSelectedTool(BuildTool.None);

		CallDeferred(nameof(SendInitialOptimizationRequest));
	}

	public override void _Process(double delta)
	{
		UpdateHudLayout();
		UpdateHoverStats(delta);
	}

	private void UpdateHoverStats(double delta)
	{
		if (_hoverLabel == null) return;

		// Ignore map placement when cursor is over HUD controls.
		if (GetViewport().GuiGetHoveredControl() != null)
		{
			_hoverLabel.Text = "";
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
			_hoverLabel.Text = "";
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
			_hoverLabel.Text = "";
		}

		if (_hoverTimer >= 1.0f)
		{
			if (_structures.TryGetValue(gridPos, out var structure))
			{
				if (structure == BuildTool.Road)
				{
					float congestion = GetRoadCongestionValue(gridPos);
					_hoverLabel.Text = $"Road ({gridPos.X},{gridPos.Y}) | Congestion: {congestion:0.0}";
				}
				else if (structure == BuildTool.Mall)
				{
					float caused = GetMallCongestionCaused(gridPos);
					_hoverLabel.Text = $"Mall ({gridPos.X},{gridPos.Y}) | Added Congestion: +{caused:0.0}";
				}
				return;
			}

			int hospitalIdx = _hospitalPositions.IndexOf(gridPos);
			if (hospitalIdx >= 0)
			{
				string facilityId = $"Hospital_{hospitalIdx}";
				int patients = GetFacilityPatients(facilityId);
				float revenue = patients * RevenuePerPatient;
				_hoverLabel.Text = $"Hospital {hospitalIdx} | Patients: {patients} | Revenue: ${revenue:0.0}";
				return;
			}

			if (gridPos == _clinicPos)
			{
				int patients = GetFacilityPatients("Clinic");
				float revenue = patients * RevenuePerPatient;
				_hoverLabel.Text = $"Clinic | Patients: {patients} | Revenue: ${revenue:0.0}";
				return;
			}

			_hoverLabel.Text = ""; // Grass tile or generic entity
		}
	}

	private float GetRoadCongestionValue(Vector2I roadPos)
	{
		float baseCongestion = ((roadPos.X * 3 + roadPos.Y) % 5) * 0.4f;
		float mallImpact = 0f;

		foreach (var kv in _structures)
		{
			if (kv.Value == BuildTool.Mall && Manhattan(kv.Key, roadPos) <= 1)
				mallImpact += 1.4f;
		}

		return baseCongestion + mallImpact;
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

	private void GenerateGrid()
	{
		_occupiedTiles.Clear();
		_hospitalPositions.Clear();
		_housePositions.Clear();
		_structures.Clear();
		_structureNodes.Clear();

		var rng = new RandomNumberGenerator();
		rng.Randomize();

		// Ground (island)
		for (int x = 0; x < GridSize; x++)
		{
			for (int y = 0; y < GridSize; y++)
			{
				Color groundColor = rng.Randf() < 0.10f
					? new Color(0.35f, 0.35f, 0.35f)
					: new Color(0.2f, 0.55f, 0.25f);

				PlaceTile(new Vector2I(x, y), groundColor, $"Tile_{x}_{y}");
			}
		}

		// Clinic
		_clinicPos = new Vector2I(GridSize / 2, GridSize / 2);
		PlaceEntity(_clinicPos, new Color(0.8f, 0.6f, 0.2f), "🌟", "Clinic");
		_occupiedTiles.Add(_clinicPos);

		// Hospitals (kept away from clinic)
		PlaceRandomEntities(
			rng,
			count: HospitalCount,
			minDist: 10f,
			exclusions: new List<Vector2I> { _clinicPos },
			col: new Color(0.8f, 0.2f, 0.2f),
			emoji: "🏥",
			prefix: "Hospital",
			storeList: _hospitalPositions
		);

		// Houses (kept away from hospitals)
		PlaceRandomEntities(
			rng,
			count: HouseCount,
			minDist: 4f,
			exclusions: _hospitalPositions,
			col: new Color(0.2f, 0.4f, 0.8f),
			emoji: "🏠",
			prefix: "House",
			storeList: _housePositions
		);
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

		while (placed < count && attempts < 1000)
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

	private void PlaceStructure(Vector2I gridPos, BuildTool tool)
	{
		_structures[gridPos] = tool;

		if (tool == BuildTool.Road)
		{
			RefreshRoadAt(gridPos);
			RefreshRoadAt(new Vector2I(gridPos.X, gridPos.Y - 1));
			RefreshRoadAt(new Vector2I(gridPos.X + 1, gridPos.Y));
			RefreshRoadAt(new Vector2I(gridPos.X, gridPos.Y + 1));
			RefreshRoadAt(new Vector2I(gridPos.X - 1, gridPos.Y));
			return;
		}

		RemoveStructureNode(gridPos);

		var rect = new ColorRect
		{
			Size = new Vector2(TileSpacing - 14, TileSpacing - 14),
			Position = new Vector2(gridPos.X * TileSpacing + 6, gridPos.Y * TileSpacing + 6),
			Color = new Color(0.75f, 0.4f, 0.15f, 0.95f),
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
	}

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
			if (string.IsNullOrEmpty(file))
				break;
			if (dir.CurrentIsDir())
				continue;
			if (!file.StartsWith("road", StringComparison.OrdinalIgnoreCase))
				continue;

			string ext = Path.GetExtension(file).ToLowerInvariant();
			if (ext != ".tga" && ext != ".png" && ext != ".webp")
				continue;

			string nameNoExt = Path.GetFileNameWithoutExtension(file);
			string suffix = nameNoExt.Length > 4 ? nameNoExt.Substring(4) : string.Empty;
			string key = NormalizeRoadKey(suffix);
			if (string.IsNullOrEmpty(key))
				continue;

			var tex = GD.Load<Texture2D>($"res://roads/{file}");
			if (tex != null)
				_roadTextures[key] = ToSingleTileTexture(tex);
		}
		dir.ListDirEnd();
	}

	private static Texture2D ToSingleTileTexture(Texture2D texture)
	{
		if (texture == null)
			return null;

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
		if (string.IsNullOrWhiteSpace(raw))
			return string.Empty;

		bool n = false, e = false, s = false, w = false;
		string upper = raw.Trim().ToUpperInvariant();

		// tokenize by non-letters/non-digits
		var token = new StringBuilder();
		void FlushToken()
		{
			if (token.Length == 0) return;
			ApplyRoadToken(token.ToString(), ref n, ref e, ref s, ref w);
			token.Clear();
		}

		foreach (char c in upper)
		{
			if (char.IsLetterOrDigit(c))
				token.Append(c);
			else
				FlushToken();
		}
		FlushToken();

		// fallback: only if entire raw is made of NESW letters (prevents "CROSS" => "S")
		bool onlyDirs = true;
		foreach (char c in upper)
		{
			if (c == '_' || c == '-' || c == ' ') continue;
			if (c is not ('N' or 'E' or 'S' or 'W'))
			{
				onlyDirs = false;
				break;
			}
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
			case "N":
			case "NORTH": n = true; return;
			case "E":
			case "EAST": e = true; return;
			case "S":
			case "SOUTH": s = true; return;
			case "W":
			case "WEST": w = true; return;

			case "NS":
			case "SN":
			case "V":
			case "VERT":
			case "VERTICAL": n = s = true; return;

			case "EW":
			case "WE":
			case "H":
			case "HOR":
			case "HORIZONTAL": e = w = true; return;

			case "NESW":
			case "CROSS":
			case "X":
			case "PLUS":
			case "INTERSECTION": n = e = s = w = true; return;
		}

		// direction combos in one token: NE, EN, T_NES-like compact forms
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

		if (TryGetRoadTexture(mask, out var tex, out float rotation))
		{
			var road = new TextureRect
			{
				Name = $"Structure_{gridPos.X}_{gridPos.Y}",
				Position = new Vector2(gridPos.X * TileSpacing + 1, gridPos.Y * TileSpacing + 1),
				Size = new Vector2(TileSpacing - 2, TileSpacing - 2),
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

			AddChild(road);
			_structureNodes[gridPos] = road;
			return;
		}

		var fallback = CreateConnectedRoadFallback(gridPos, mask);
		AddChild(fallback);
		_structureNodes[gridPos] = fallback;
	}

	private void RemoveStructureNode(Vector2I gridPos)
	{
		if (_structureNodes.TryGetValue(gridPos, out var oldNode))
		{
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

	private bool IsRoadAt(Vector2I p)
	{
		return _structures.TryGetValue(p, out var t) && t == BuildTool.Road;
	}

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
		if (_roadTextures.TryGetValue(key, out texture))
			return true;

		if ((key == "NS" || key == "N" || key == "S") && _roadTextures.TryGetValue("EW", out texture))
		{
			rotation = Mathf.Pi * 0.5f;
			return true;
		}

		if ((key == "E" || key == "W") && _roadTextures.TryGetValue("EW", out texture))
			return true;

		return false;
	}

	private Control CreateConnectedRoadFallback(Vector2I gridPos, int mask)
	{
		float size = TileSpacing - 2;
		float thickness = Mathf.Max(10f, Mathf.Round(size * 0.36f));
		float center = (size - thickness) * 0.5f;

		var root = new Control
		{
			Name = $"Structure_{gridPos.X}_{gridPos.Y}",
			Position = new Vector2(gridPos.X * TileSpacing + 1, gridPos.Y * TileSpacing + 1),
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

	private void CreateHud()
	{
		var layer = new CanvasLayer();
		layer.Name = "HUDLayer";
		AddChild(layer);

		_infoPanel = new PanelContainer
		{
			CustomMinimumSize = new Vector2(320, 140)
		};
		layer.AddChild(_infoPanel);

		var infoRoot = new VBoxContainer();
		_infoPanel.AddChild(infoRoot);

		_dayLabel = new Label();
		_playerBudgetLabel = new Label();
		_optimizerBudgetLabel = new Label();
		_previousProfitLabel = new Label();
		_toolLabel = new Label();
		_statusLabel = new Label { CustomMinimumSize = new Vector2(360, 56) };
		_statusLabel.AutowrapMode = TextServer.AutowrapMode.WordSmart;
		_hoverLabel = new Label { CustomMinimumSize = new Vector2(360, 40) };
		_hoverLabel.AutowrapMode = TextServer.AutowrapMode.WordSmart;

		infoRoot.AddChild(_dayLabel);
		infoRoot.AddChild(_playerBudgetLabel);
		infoRoot.AddChild(_optimizerBudgetLabel);
		infoRoot.AddChild(_previousProfitLabel);

		_actionsPanel = new PanelContainer
		{
			CustomMinimumSize = new Vector2(360, 300)
		};
		layer.AddChild(_actionsPanel);

		var actionsRoot = new VBoxContainer();
		_actionsPanel.AddChild(actionsRoot);

		actionsRoot.AddChild(new Label { Text = "Actions" });

		_pointerButton = new Button { Text = "Pointer", ToggleMode = true };
		_pointerButton.Pressed += () => SetSelectedTool(BuildTool.None);
		actionsRoot.AddChild(_pointerButton);

		actionsRoot.AddChild(new Label { Text = "Road Management" });
		_roadBuildButton = new Button { Text = $"Build Road (${RoadCost:0})", ToggleMode = true };
		_roadDestroyButton = new Button { Text = "Destroy Road" };
		_roadUpgradeButton = new Button { Text = "Upgrade Road" };
		_roadBuildButton.Pressed += () => SetSelectedTool(BuildTool.Road);
		_roadDestroyButton.Pressed += () => ShowUnavailableAction("Road destroy is not implemented yet.");
		_roadUpgradeButton.Pressed += () => ShowUnavailableAction("Road upgrade is not implemented yet.");
		actionsRoot.AddChild(_roadBuildButton);
		actionsRoot.AddChild(_roadDestroyButton);
		actionsRoot.AddChild(_roadUpgradeButton);

		actionsRoot.AddChild(new Label { Text = "Infrastructure" });
		_mallBuildButton = new Button { Text = $"Build Mall (${MallCost:0})", ToggleMode = true };
		_mallDestroyButton = new Button { Text = "Destroy Mall" };
		_mallBuildButton.Pressed += () => SetSelectedTool(BuildTool.Mall);
		_mallDestroyButton.Pressed += () => ShowUnavailableAction("Mall destroy is not implemented yet.");
		actionsRoot.AddChild(_mallBuildButton);
		actionsRoot.AddChild(_mallDestroyButton);

		actionsRoot.AddChild(new Label { Text = "Temporary Actions" });
		_cutHospitalFundingButton = new Button { Text = "Cut Hospital Funding" };
		_roadBlockageButton = new Button { Text = "Road Blockage" };
		_cutHospitalFundingButton.Pressed += () => ShowUnavailableAction("Cut Hospital Funding is not implemented yet.");
		_roadBlockageButton.Pressed += () => ShowUnavailableAction("Road Blockage is not implemented yet.");
		actionsRoot.AddChild(_cutHospitalFundingButton);
		actionsRoot.AddChild(_roadBlockageButton);

		actionsRoot.AddChild(_toolLabel);

		_endTurnButton = new Button { Text = "End Turn (Send to GAMSPy)" };
		_endTurnButton.Pressed += EndTurn;
		actionsRoot.AddChild(_endTurnButton);

		actionsRoot.AddChild(_statusLabel);
		actionsRoot.AddChild(_hoverLabel);

		UpdateHudLayout();
	}

	private void UpdateHudLayout()
	{
		if (_infoPanel == null || _actionsPanel == null)
			return;

		float viewportWidth = GetViewportRect().Size.X;
		float infoWidth = _infoPanel.Size.X > 0f
			? _infoPanel.Size.X
			: _infoPanel.CustomMinimumSize.X;

		_infoPanel.Position = new Vector2(viewportWidth - infoWidth - 12f, 12f);

		float viewportHeight = GetViewportRect().Size.Y;
		float actionsHeight = _actionsPanel.Size.Y > 0f
			? _actionsPanel.Size.Y
			: _actionsPanel.CustomMinimumSize.Y;

		_actionsPanel.Position = new Vector2(12, viewportHeight - actionsHeight - 12f);
	}

	private void SetSelectedTool(BuildTool tool)
	{
		_selectedTool = tool;

		_pointerButton.ButtonPressed = tool == BuildTool.None;
		_roadBuildButton.ButtonPressed = tool == BuildTool.Road;
		_mallBuildButton.ButtonPressed = tool == BuildTool.Mall;

		_toolLabel.Text = $"Selected: {ToolName(tool)}";
	}

	private static string ToolName(BuildTool tool)
	{
		return tool switch
		{
			BuildTool.Road => "Road",
			BuildTool.Mall => "Mall",
			_ => "Pointer"
		};
	}

	private void UpdateHud()
	{
		if (_dayLabel == null) return;

		_dayLabel.Text = $"Turn Count: {_day}";
		_playerBudgetLabel.Text = $"Current Budget: ${_playerBudget:0.0}";
		_optimizerBudgetLabel.Text = $"Optimizer Budget: ${_citizenBudget:0.0}";
		_previousProfitLabel.Text = $"Previous Day's Profit: ${_previousDayProfit:0.0}";
	}

	private float GetToolCost(BuildTool tool)
	{
		return tool switch
		{
			BuildTool.Road => RoadCost,
			BuildTool.Mall => MallCost,
			_ => 0f
		};
	}

	private void TryPlaceSelectedToolAtMouse()
	{
		if (_gameOver || _turnInProgress) return;
		if (_selectedTool == BuildTool.None) return;

		// Ignore map placement when cursor is over HUD controls.
		if (GetViewport().GuiGetHoveredControl() != null)
			return;

		Vector2 mouseWorld = GetGlobalMousePosition();
		Vector2I gridPos = new(
			Mathf.FloorToInt(mouseWorld.X / TileSpacing),
			Mathf.FloorToInt(mouseWorld.Y / TileSpacing)
		);

		if (gridPos.X < 0 || gridPos.X >= GridSize || gridPos.Y < 0 || gridPos.Y >= GridSize)
			return;

		if (_occupiedTiles.Contains(gridPos))
		{
			_statusLabel.Text = "Cannot build on clinic/hospital/house.";
			return;
		}

		if (_structures.ContainsKey(gridPos))
		{
			_statusLabel.Text = "Tile already has a structure.";
			return;
		}

		float cost = GetToolCost(_selectedTool);
		if (_playerBudget < cost)
		{
			_statusLabel.Text = "Not enough budget.";
			return;
		}

		_playerBudget -= cost;

		PlaceStructure(gridPos, _selectedTool);
		UpdateHud();

		_statusLabel.Text = $"{ToolName(_selectedTool)} built at ({gridPos.X}, {gridPos.Y}).";
		CheckLoseCondition();
	}

	private void SendInitialOptimizationRequest()
	{
		_statusLabel.Text = "Initial handoff to GAMSPy...";
		_resolveTurnAfterResponse = false;
		SendOptimizationRequest();
	}

	private void EndTurn()
	{
		if (_gameOver || _turnInProgress)
			return;

		_turnInProgress = true;
		_endTurnButton.Disabled = true;
		_resolveTurnAfterResponse = true;

		_statusLabel.Text = "Turn ended. Sending to GAMSPy...";
		SendOptimizationRequest();
	}

	private void SendOptimizationRequest()
	{
		var payload = BuildOptimizationPayload();
		string json = JsonSerializer.Serialize(payload);
		string[] headers = { "Content-Type: application/json" };

		Error err = _http.Request(
			"http://127.0.0.1:8000/solve",
			headers,
			HttpClient.Method.Post,
			json
		);

		if (err != Error.Ok)
		{
			GD.PrintErr($"Failed to send optimization request: {err}");
			_statusLabel.Text = $"HTTP error: {err}";
			_turnInProgress = false;
			_endTurnButton.Disabled = _gameOver;
		}
		else
		{
			GD.Print("Sent optimization request:");
			GD.Print(json);
		}
	}

	private SolveRequest BuildOptimizationPayload()
	{
		var facilities = new List<FacilityData>
		{
			new FacilityData
			{
				id = "Clinic",
				type = "clinic",
				x = _clinicPos.X,
				y = _clinicPos.Y
			}
		};

		for (int i = 0; i < _hospitalPositions.Count; i++)
		{
			var hp = _hospitalPositions[i];
			facilities.Add(new FacilityData
			{
				id = $"Hospital_{i}",
				type = "hospital",
				x = hp.X,
				y = hp.Y
			});
		}

		var houses = new List<HouseData>();
		var houseAccess = new List<FacilityCost>();
		var existingRoads = new List<GridPoint>();

		for (int i = 0; i < _housePositions.Count; i++)
		{
			string houseId = $"House_{i}";
			Vector2I housePos = _housePositions[i];
			houses.Add(new HouseData
			{
				id = houseId,
				x = housePos.X,
				y = housePos.Y
			});

			houseAccess.Add(new FacilityCost
			{
				house = houseId,
				facility = "Clinic",
				travel_cost = ComputeTravelCost(housePos, _clinicPos, isClinic: true)
			});

			for (int h = 0; h < _hospitalPositions.Count; h++)
			{
				houseAccess.Add(new FacilityCost
				{
					house = houseId,
					facility = $"Hospital_{h}",
					travel_cost = ComputeTravelCost(housePos, _hospitalPositions[h], isClinic: false)
				});
			}
		}

		foreach (var kv in _structures)
		{
			if (kv.Value != BuildTool.Road)
				continue;

			existingRoads.Add(new GridPoint
			{
				x = kv.Key.X,
				y = kv.Key.Y
			});
		}

		return new SolveRequest
		{
			houses = houses,
			facilities = facilities,
			house_access = houseAccess,
			existing_roads = existingRoads,
			budget = Mathf.Min(_playerBudget, _clinicMoney),
			road_cost = RoadCost,
			turn_index = _day,
			setup_phase = !_resolveTurnAfterResponse
		};
	}

	private float ComputeTravelCost(Vector2I from, Vector2I to, bool isClinic)
	{
		float dist = Manhattan(from, to);
		float congestion = ((from.X * 3 + from.Y + to.X + to.Y) % 5) * 0.4f;
		float clinicPenalty = isClinic ? 1.2f : 0f;

		// Citizen side pressure each day: favors hospitals, hurts clinic.
		float citizenBias = isClinic ? _citizenClinicPenalty : -_citizenHospitalBonus;

		// Player-built structures affect route cost.
		float structureDelta = 0f;
		foreach (var kv in _structures)
		{
			Vector2I p = kv.Key;
			BuildTool t = kv.Value;

			if (t == BuildTool.Road && IsOnManhattanRoute(p, from, to))
				structureDelta -= 0.9f;

			if (t == BuildTool.Mall && IsNearManhattanRoute(p, from, to))
				structureDelta += 1.4f;
		}

		float total = dist + congestion + clinicPenalty + citizenBias + structureDelta;
		return Mathf.Max(1f, total);
	}

	private void OnRequestCompleted(long result, long responseCode, string[] headers, byte[] body)
	{
		string responseText = Encoding.UTF8.GetString(body);
		GD.Print($"Optimizer response code: {responseCode}");
		GD.Print(responseText);

		if (result != (long)HttpRequest.Result.Success)
		{
			string message = $"Optimizer request failed: {(HttpRequest.Result)result}";
			if (!string.IsNullOrWhiteSpace(responseText))
				message += $" | {responseText}";

			GD.PrintErr(message);
			_statusLabel.Text = message;
			_turnInProgress = false;
			_endTurnButton.Disabled = _gameOver;
			return;
		}

		if (responseCode < 200 || responseCode >= 300)
		{
			string message = $"Optimizer returned HTTP {responseCode}";
			if (!string.IsNullOrWhiteSpace(responseText))
				message += $" | {responseText}";

			GD.PrintErr(message);
			_statusLabel.Text = message;
			_turnInProgress = false;
			_endTurnButton.Disabled = _gameOver;
			return;
		}

		if (string.IsNullOrWhiteSpace(responseText))
		{
			const string message = "Optimizer returned an empty response body.";
			GD.PrintErr(message);
			_statusLabel.Text = message;
			_turnInProgress = false;
			_endTurnButton.Disabled = _gameOver;
			return;
		}

		SolveResponse response = null;
		try
		{
			response = JsonSerializer.Deserialize<SolveResponse>(responseText);
		}
		catch (Exception e)
		{
			string message = $"Failed to parse optimizer response: {e.Message}";
			GD.PrintErr(message);
			_statusLabel.Text = message;
			_turnInProgress = false;
			_endTurnButton.Disabled = _gameOver;
			return;
		}

		int roadsBuilt = ApplyOptimizerRoads(response);
		float roadSpendCap = roadsBuilt * RoadCost;
		float roadSpend = Mathf.Min(response?.spent_budget ?? 0f, roadSpendCap);

		UpdateFacilityPatientCounts(response);

		if (_resolveTurnAfterResponse)
		{
			ResolveTurn(response, roadSpend, roadsBuilt);
			_resolveTurnAfterResponse = false;
		}
		else
		{
			_statusLabel.Text = roadsBuilt > 0
				? $"Initial optimization done. GAMSPy built {roadsBuilt} road(s). Build and press End Turn."
				: "Initial optimization done. Build and press End Turn.";
		}

		_turnInProgress = false;
		_endTurnButton.Disabled = _gameOver;
		UpdateHud();
	}

	private void ResolveTurn(SolveResponse response, float roadSpend, int roadsBuilt)
	{
		_day++;

		int clinicCount = ExtractClinicCount(response);
		float clinicRatio = response != null ? response.clinic_ratio : 0f;

		float income = clinicCount * RevenuePerPatient;
		float upkeep = DailyClinicUpkeep + Mathf.Min(_day * DailyUpkeepGrowth, 12f);
		_previousDayProfit = income - upkeep - roadSpend;

		_playerBudget -= roadSpend;
		_clinicMoney -= roadSpend;
		_clinicMoney += income - upkeep;
		_playerBudget += income;

		ApplyCitizenCounterTurn();

		if (CheckLoseCondition())
			return;

		_statusLabel.Text =
			$"Day {_day}: clinic patients={clinicCount}, ratio={clinicRatio:0.00}, roads built={roadsBuilt}, road spend=${roadSpend:0.0}, income=${income:0.0}, upkeep=${upkeep:0.0}.";
	}

	private int ExtractClinicCount(SolveResponse response)
	{
		if (response == null) return 0;
		if (response.clinic_count > 0) return response.clinic_count;

		int count = 0;
		if (response.preferred_facility != null)
		{
			foreach (var kv in response.preferred_facility)
			{
				if (kv.Value == "Clinic")
					count++;
			}
		}
		return count;
	}

	private void ApplyCitizenCounterTurn()
	{
		// Citizen side gets and spends budget each day to optimize for itself.
		_citizenBudget += 2f + (_housePositions.Count * 0.5f);

		const float spend = 4f;
		if (_citizenBudget >= spend)
		{
			_citizenBudget -= spend;
			_citizenHospitalBonus = Mathf.Min(_citizenHospitalBonus + 0.30f, 4f);
			_citizenClinicPenalty = Mathf.Min(_citizenClinicPenalty + 0.20f, 3f);
		}
	}

	private bool CheckLoseCondition()
	{
		if (_clinicMoney > 0f)
			return false;

		_gameOver = true;
		_endTurnButton.Disabled = true;
		_pointerButton.Disabled = true;
		_roadBuildButton.Disabled = true;
		_roadDestroyButton.Disabled = true;
		_roadUpgradeButton.Disabled = true;
		_mallBuildButton.Disabled = true;
		_mallDestroyButton.Disabled = true;
		_cutHospitalFundingButton.Disabled = true;
		_roadBlockageButton.Disabled = true;

		_statusLabel.Text = $"Game Over. Clinic ran out of money. Final score: {_day} days survived.";
		return true;
	}

	private static int Manhattan(Vector2I a, Vector2I b)
	{
		return Mathf.Abs(a.X - b.X) + Mathf.Abs(a.Y - b.Y);
	}

	private static bool IsOnManhattanRoute(Vector2I p, Vector2I a, Vector2I b)
	{
		return Manhattan(a, p) + Manhattan(p, b) == Manhattan(a, b);
	}

	private static bool IsNearManhattanRoute(Vector2I p, Vector2I a, Vector2I b)
	{
		return Manhattan(a, p) + Manhattan(p, b) <= Manhattan(a, b) + 1;
	}

	private float Distance(Vector2I a, Vector2I b)
	{
		return Mathf.Sqrt(Mathf.Pow(a.X - b.X, 2) + Mathf.Pow(a.Y - b.Y, 2));
	}

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
		}
		else if (@event is InputEventMouseMotion mm && _isDragging)
		{
			_camera.Position -= mm.Relative / _camera.Zoom;
		}
	}

	private void UpdateFacilityPatientCounts(SolveResponse response)
	{
		_facilityPatientCounts.Clear();

		if (response?.preferred_facility == null)
			return;

		foreach (var kv in response.preferred_facility)
		{
			string facilityId = kv.Value;
			if (string.IsNullOrEmpty(facilityId))
				continue;

			if (_facilityPatientCounts.TryGetValue(facilityId, out int current))
				_facilityPatientCounts[facilityId] = current + 1;
			else
				_facilityPatientCounts[facilityId] = 1;
		}

		if (response.clinic_count >= 0)
			_facilityPatientCounts["Clinic"] = response.clinic_count;
	}

	private void ShowUnavailableAction(string message)
	{
		_statusLabel.Text = message;
		SetSelectedTool(BuildTool.None);
	}

	private int ApplyOptimizerRoads(SolveResponse response)
	{
		if (response?.built_roads == null)
			return 0;

		int applied = 0;
		foreach (var road in response.built_roads)
		{
			Vector2I pos = new(road.x, road.y);
			if (!IsBuildableRoadTile(pos))
				continue;

			PlaceStructure(pos, BuildTool.Road);
			applied++;
		}

		return applied;
	}

	private bool IsBuildableRoadTile(Vector2I pos)
	{
		if (pos.X < 0 || pos.X >= GridSize || pos.Y < 0 || pos.Y >= GridSize)
			return false;

		if (_occupiedTiles.Contains(pos))
			return false;

		return !_structures.ContainsKey(pos);
	}
}

// Request/response models

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
}
