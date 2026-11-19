extends SceneTree

var previous: Environment
var phase := 0
var started := Time.get_ticks_msec()
var scenario := ""
var next_server := ""
var rotation_marker := ""
var opt_in: CheckBox

func _initialize() -> void:
	var args := OS.get_cmdline_user_args()
	scenario = args[0]
	if args.size() > 1:
		next_server = args[1]
	if args.size() > 2:
		rotation_marker = args[2]
	root.add_child.call_deferred(load("res://Main.tscn").instantiate())

func find_visual_checkbox(node: Node) -> CheckBox:
	if node is CheckBox and node.text == "Use server map visual suggestions":
		return node
	for child in node.get_children():
		var found := find_visual_checkbox(child)
		if found != null:
			return found
	return null

func _process(_delta: float) -> bool:
	if Time.get_ticks_msec() - started > 90000:
		printerr("Map visual check timed out: ", scenario, " phase=", phase)
		quit(2)
		return false
	if scenario == "remote-rotation" and phase == 1 and FileAccess.file_exists(rotation_marker):
		phase = 2
		root.find_child("Shell", true, false).call("ConnectToServer", next_server)
	var world := root.find_child("WorldEnvironment", true, false) as WorldEnvironment
	if world == null or world.environment == null:
		return false
	var env := world.environment
	var fixture_density := env.fog_density > 0.0 and env.fog_density < 0.001
	var sky := env.background_mode == Environment.BG_SKY
	var mounted := fixture_density and env.fog_enabled and sky and not env.glow_enabled
	var suggested := fixture_density and not env.fog_enabled and not sky and env.glow_enabled
	match scenario:
		"listen":
			if phase == 0 and fixture_density and env.fog_enabled and not sky and env.glow_enabled:
				print("PASS listen authored fog and both visual defaults")
				previous = env
				phase = 1
				root.find_child("ListenServer", true, false).call("RequestMapChange", "stormkeep")
			elif phase == 1 and env != previous and sky and not env.glow_enabled and not env.fog_enabled:
				print("PASS listen map transition restored sky/bloom defaults and cleared fog")
				quit(0)
		"pins":
			if fixture_density and not env.fog_enabled and sky and env.glow_enabled:
				print("PASS listen explicit sky/fog preferences override map values")
				quit(0)
		"remote-default":
			if mounted:
				print("PASS opt-out used mounted sky, bloom and depth-fog defaults with the server's fog parameters")
				quit(0)
		"remote-on":
			var welcome := root.find_child("DialogWelcome", true, false) as Control
			if suggested and welcome != null and welcome.is_visible_in_tree():
				print("PASS opt-in used dedicated sky, bloom and depth-fog suggestions and opened the server Welcome")
				quit(0)
		"remote-pins":
			if mounted:
				print("PASS explicit sky, bloom and depth-fog preferences overrode all three dedicated suggestions")
				quit(0)
		"remote-acl":
			if phase == 0 and suggested:
				root.find_child("Console", true, false).call("Submit", 'set g_mapinfo_settemp_acl "-r_sky -r_drawfog +*"')
				phase = 1
			elif phase == 1 and fixture_density and env.fog_enabled and sky and env.glow_enabled:
				print("PASS local ACL withdrew sky and depth-fog suggestions and retained bloom")
				root.find_child("Console", true, false).call("Submit", 'set g_mapinfo_settemp_acl "+*"')
				phase = 2
			elif phase == 2 and suggested:
				print("PASS loosening the ACL restored both suggestions in the same session")
				quit(0)
		"remote-hold":
			if mounted and phase == 0:
				print("PASS concurrent opt-out peer used mounted defaults")
				phase = 1
			if phase == 1 and not mounted:
				printerr("Concurrent opt-out peer changed visuals")
				quit(2)
			elif phase == 1 and FileAccess.file_exists(next_server):
				quit(0)
		"remote-bad-tail":
			if mounted and FileAccess.file_exists(next_server):
				phase += 1
				if phase == 30:
					print("PASS opted-in client kept mounted defaults with no valid suggestion tail")
					quit(0)
			else:
				phase = 0
		"remote-toggle":
			if phase == 0 and mounted:
				root.find_child("Shell", true, false).call("OpenDebugScreen", "settings:Effects")
				phase = 1
			elif phase == 1:
				opt_in = find_visual_checkbox(root)
				if opt_in != null and not opt_in.button_pressed:
					opt_in.button_pressed = true
					phase = 2
			elif phase == 2 and suggested:
				print("PASS native Effects checkbox enabled suggestions in the current session")
				opt_in.button_pressed = false
				phase = 3
			elif phase == 3 and mounted and FileAccess.file_exists(next_server):
				print("PASS clearing the Effects checkbox restored mounted defaults in the current session")
				quit(0)
		"remote-rotation":
			if phase == 0 and suggested:
				print("PASS remote received darkzone suggestions before rotation")
				previous = env
				phase = 1
			elif phase == 2 and env != previous and fixture_density and not env.fog_enabled and sky and env.glow_enabled:
				print("PASS reconnect after dedicated rotation discarded darkzone suggestions, used mounted bloom and honored server fog none")
				quit(0)
	return false
