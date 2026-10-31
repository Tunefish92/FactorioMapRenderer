# Changelog

## 0.1.3 – 2026-10-31

* Smoother zooming: the map no longer makes the browser lay out the page again for every tile layer and every label
  at each step of a zoom, the game graphics and the alt mode come in larger tiles (a quarter as many pieces for the
  browser to put together at every frame), and the alt mode and chunk grid are only drawn for the zoom a zoom ends at.
* The tick box that shows or hides all layers of a group is no longer inside the group's folding header (invalid
  HTML that browsers warned about, and out of reach of screen readers); it looks and works as before.

## 0.1.2 – 2026-10-31

* Folder and SSH: a save Factorio is still writing (`name.tmp.zip`), or one left over from a save that was cut off,
  is no longer taken as the newest save; such a file failed with "not a zip archive".

## 0.1.1 – 2026-10-29

* Supported versions made explicit: saves from Factorio 1.0 up to 2.1.24 (tested). A save of a newer version is
  shown as untested (a badge in the status bar, a note in the details); one older than 1.0 is refused at once with
  what to do about it.
* A new map nobody has joined yet (on a server) shows its generated starting area instead of nothing, and the map
  seed is always in the details.
* A save from a folder or over SSH is shown under its own name, not as the snapshot the renderer copied it to.
* Star map: zooming and dragging at the same time no longer sends the planets off the screen, and a quick flick
  no longer throws the map away (on every surface the swing after a drag is limited).
* Smoother tiles everywhere: switching to a surface no longer loads tiles of the place left behind; zooming into the
  game's graphics keeps the map until they are drawn; a ring of tiles around the view is drawn ahead, and tiles drawn
  lately come back at once; the star map is built once.

## 0.1.0 – 2026-10-29

* An interactive map of a Factorio save in the browser: every planet and space platform as far as it is
  charted, in the colours of the game's own map view.
* The game reads the save itself: the renderer runs Factorio with a small export mod for one tick, so the map
  works for every game version from 2.0 on. Without a game, Factorio's free headless server is downloaded.
* The factory in layers: buildings with their icons when zoomed in, belts, pipes, rails, the power grid with its
  copper wires, enemy structures, resources, trees, rocks and cliffs.
* Zoomed in, the game's own graphics as layers of their own: the ground in the game's textures, and buildings,
  belts, pipes, rails and signals, ore, trees, rocks, cliffs and nests as the game draws them, up to its full
  resolution. Inserters with their arms, turrets with their heads, the rocket silo with its doors, space platform
  hubs, cargo bays and landing pads, copper wires hanging from pole to pole.
* The game's alt mode: over each machine its recipe and modules, the ore a drill mines, the filters of inserters,
  splitters and loaders, the modules in beacons.
* Smooth on every device: the close-up graphics are drawn by background workers, the files of a render are kept
  by the browser and sent compressed, and the star map zooms without steps.
* Works on phones and tablets: the panel over the map, the planets along its bottom edge, larger buttons for
  fingers.
* The solar system: the star map with every planet, the routes between them and every space platform; a click
  opens a planet's or platform's map.
* Everything the page shows comes with the image, Factorio's headless server included: nothing is loaded from
  other sites.
* A click on a building shows its recipe, status, quality and contents; markers for players, train stops, map
  tags, vehicles, rocket silos, landing pads and platform hubs, resource patches and enemy bases.
* Statistics per surface: what was produced there, and how many of each building there are.
* The save game is chosen in the page: upload, the newest save of a folder, or over SSH. With the server's RCON
  the running game is saved first, and the Server tab shows the server's state and has a console for admins.
* While the map is rendered, every page shows how far it has come over the blurred map: the steps with their
  times, what is left, and the server's console; then the new map opens by itself.
