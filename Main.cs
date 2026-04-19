using Godot;
using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;

public partial class Main : Node2D
{
	private Camera2D _camera;
	private HttpRequest _http;
	private bool _isDragging = false;
	private Texture2D _roadEWTexture;

	private const int GridSize = 20;
	private const int TileSpacing = 64;

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

	private float _citizenHospitalBonus = 0f; // lowers hospital cost over time
	private float _citizenClinicPenalty = 0f; // raises clinic cost over time

	private bool _turnInProgress = false;
	private bool _resolveTurnAfterResponse = false;
	private bool _gameOver = false;

	// HUD
	private Label _dayLabel;
	private Label _playerBudgetLabel;
	private Label _citizenBudgetLabel;
	private Label _clinicMoneyLabel;
	private Label _toolLabel;
	private Label _statusLabel;

	private Button _pointerButton;
	private Button _roadButton;
	private Button _mallButton;
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

		_roadEWTexture = GD.Load<Texture2D>("res://roads/roadEW.tga");
		if (_roadEWTexture == null)
			GD.PrintErr("Missing road texture: res://roads/roadEW.tga");

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
		if (_structureNodes.TryGetValue(gridPos, out var oldNode))
		{
			oldNode.QueueFree();
			_structureNodes.Remove(gridPos);
		}

		Control nodeToStore;

		if (tool == BuildTool.Road && _roadEWTexture != null)
		{
			var road = new TextureRect
			{
				Name = $"Structure_{gridPos.X}_{gridPos.Y}",
				Position = new Vector2(gridPos.X * TileSpacing + 1, gridPos.Y * TileSpacing + 1),
				Size = new Vector2(TileSpacing - 2, TileSpacing - 2),
				Texture = _roadEWTexture,
				StretchMode = TextureRect.StretchModeEnum.Scale,
				ZIndex = 2,
				MouseFilter = Control.MouseFilterEnum.Ignore
			};

			AddChild(road);
			nodeToStore = road;
		}
		else
		{
			var rect = new ColorRect
			{
				Size = new Vector2(TileSpacing - 14, TileSpacing - 14),
				Position = new Vector2(gridPos.X * TileSpacing + 6, gridPos.Y * TileSpacing + 6),
				Color = tool == BuildTool.Road
					? new Color(0.3f, 0.3f, 0.3f, 0.95f)
					: new Color(0.75f, 0.4f, 0.15f, 0.95f),
				Name = $"Structure_{gridPos.X}_{gridPos.Y}",
				ZIndex = 2,
				MouseFilter = Control.MouseFilterEnum.Ignore
			};

			var label = new Label
			{
				Text = tool == BuildTool.Road ? "🛣️" : "🛍️",
				Size = rect.Size,
				HorizontalAlignment = HorizontalAlignment.Center,
				VerticalAlignment = VerticalAlignment.Center
			};
			label.AddThemeFontSizeOverride("font_size", 24);

			rect.AddChild(label);
			AddChild(rect);
			nodeToStore = rect;
		}

		_structures[gridPos] = tool;
		_structureNodes[gridPos] = nodeToStore;
	}

	private void CreateHud()
	{
		var layer = new CanvasLayer();
		layer.Name = "HUDLayer";
		AddChild(layer);

		var panel = new PanelContainer
		{
			Position = new Vector2(12, 12),
			CustomMinimumSize = new Vector2(380, 280)
		};
		layer.AddChild(panel);

		var root = new VBoxContainer();
		panel.AddChild(root);

		_dayLabel = new Label();
		_playerBudgetLabel = new Label();
		_citizenBudgetLabel = new Label();
		_clinicMoneyLabel = new Label();
		_toolLabel = new Label();
		_statusLabel = new Label { CustomMinimumSize = new Vector2(360, 56) };
		_statusLabel.AutowrapMode = TextServer.AutowrapMode.WordSmart;

		root.AddChild(_dayLabel);
		root.AddChild(_clinicMoneyLabel);
		root.AddChild(_playerBudgetLabel);
		root.AddChild(_citizenBudgetLabel);

		var toolsTitle = new Label { Text = "Building Tools:" };
		root.AddChild(toolsTitle);

		var toolRow = new HBoxContainer();
		root.AddChild(toolRow);

		_pointerButton = new Button { Text = "Pointer", ToggleMode = true };
		_roadButton = new Button { Text = $"Road (${RoadCost:0})", ToggleMode = true };
		_mallButton = new Button { Text = $"Mall (${MallCost:0})", ToggleMode = true };

		_pointerButton.Pressed += () => SetSelectedTool(BuildTool.None);
		_roadButton.Pressed += () => SetSelectedTool(BuildTool.Road);
		_mallButton.Pressed += () => SetSelectedTool(BuildTool.Mall);

		toolRow.AddChild(_pointerButton);
		toolRow.AddChild(_roadButton);
		toolRow.AddChild(_mallButton);

		root.AddChild(_toolLabel);

		_endTurnButton = new Button { Text = "End Turn (Send to GAMSPy)" };
		_endTurnButton.Pressed += EndTurn;
		root.AddChild(_endTurnButton);

		root.AddChild(_statusLabel);
	}

	private void SetSelectedTool(BuildTool tool)
	{
		_selectedTool = tool;

		_pointerButton.ButtonPressed = tool == BuildTool.None;
		_roadButton.ButtonPressed = tool == BuildTool.Road;
		_mallButton.ButtonPressed = tool == BuildTool.Mall;

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

		_dayLabel.Text = $"Days Survived: {_day}";
		_clinicMoneyLabel.Text = $"Clinic Money: ${_clinicMoney:0.0}";
		_playerBudgetLabel.Text = $"Your Budget: ${_playerBudget:0.0}";
		_citizenBudgetLabel.Text = $"Citizen Budget: ${_citizenBudget:0.0}";
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
		if (_playerBudget < cost || _clinicMoney < cost)
		{
			_statusLabel.Text = "Not enough budget.";
			return;
		}

		_playerBudget -= cost;
		_clinicMoney -= cost;

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

		var houses = new List<string>();
		var houseAccess = new List<FacilityCost>();

		for (int i = 0; i < _housePositions.Count; i++)
		{
			string houseId = $"House_{i}";
			Vector2I housePos = _housePositions[i];
			houses.Add(houseId);

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

		return new SolveRequest
		{
			houses = houses,
			facilities = facilities,
			house_access = houseAccess,
			budget = _playerBudget
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

		SolveResponse response = null;
		try
		{
			response = JsonSerializer.Deserialize<SolveResponse>(responseText);
		}
		catch (Exception e)
		{
			GD.PrintErr($"Failed to parse optimizer response: {e.Message}");
		}

		if (_resolveTurnAfterResponse)
		{
			ResolveTurn(response);
			_resolveTurnAfterResponse = false;
		}
		else
		{
			_statusLabel.Text = "Initial optimization done. Build and press End Turn.";
		}

		_turnInProgress = false;
		_endTurnButton.Disabled = _gameOver;
		UpdateHud();
	}

	private void ResolveTurn(SolveResponse response)
	{
		_day++;

		int clinicCount = ExtractClinicCount(response);
		float clinicRatio = response != null ? response.clinic_ratio : 0f;

		float income = clinicCount * RevenuePerPatient;
		float upkeep = DailyClinicUpkeep + Mathf.Min(_day * DailyUpkeepGrowth, 12f);

		_clinicMoney += income - upkeep;
		_playerBudget += income;

		ApplyCitizenCounterTurn();

		if (CheckLoseCondition())
			return;

		_statusLabel.Text =
			$"Day {_day}: clinic patients={clinicCount}, ratio={clinicRatio:0.00}, income=${income:0.0}, upkeep=${upkeep:0.0}.";
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
		_roadButton.Disabled = true;
		_mallButton.Disabled = true;

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
}

// Request/response models

public class SolveRequest
{
	public List<string> houses { get; set; }
	public List<FacilityData> facilities { get; set; }
	public List<FacilityCost> house_access { get; set; }
	public float budget { get; set; }
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

public class SolveResponse
{
	public Dictionary<string, string> preferred_facility { get; set; }
	public int clinic_count { get; set; }
	public float clinic_ratio { get; set; }
	public bool clinic_alive { get; set; }
	public float citizen_cost_objective { get; set; }
	public string solver_status { get; set; }
	public float budget_seen { get; set; }
}
