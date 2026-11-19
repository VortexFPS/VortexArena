#!/usr/bin/env python3
"""Run the row 93 Stormkeep capture with disposable X11 and PulseAudio profiles."""

import argparse
import os
import re
import struct
import subprocess as sp
import time
import zipfile
from pathlib import Path

cli = argparse.ArgumentParser(description=__doc__)
cli.add_argument("engine", choices=["stock", "native"])
cli.add_argument("phase", choices=["timed", "early", "overlap"])
cli.add_argument("--output", type=Path, required=True, help="new directory under /tmp")
cli.add_argument("--repo", type=Path, default=Path(__file__).resolve().parents[4])
cli.add_argument("--baseline", type=Path, default=Path("/tmp/vortex-parity-base"))
cli.add_argument("--stock", type=Path, default=Path("/tmp/va-reviewed-stock"))
cli.add_argument("--godot", type=Path, help="Godot 4.6.3 Mono executable")
cli.add_argument("--resolution", default="640x448", help="client resolution, for example 480x320")
cli.add_argument("--display-offset", type=int, default=0, help="offset for X11 displays and UDP ports")
args = cli.parse_args()
if not re.fullmatch(r"[1-9][0-9]*x[1-9][0-9]*", args.resolution):
    cli.error("--resolution uses WIDTHxHEIGHT")
width, height = args.resolution.split("x")

repo = args.repo.resolve()
run = args.output.resolve()
base = args.baseline.resolve()
stock = args.stock.resolve()
native = args.engine == "native"
overlap = args.phase == "overlap"
single_pickup = native and args.phase == "timed"
spawn_shield = not native or args.phase == "early"
if args.phase == "early" and not native:
    cli.error("the stock timed phase also captures its second pickup and early removal")
pin = sp.check_output(["git", "-C", base, "rev-parse", "HEAD"], text=True).strip()
if pin != "863cd3e8456e18f577f7fe5fef857ff55ae62cc0":
    cli.error(f"unexpected baseline revision {pin}")
godot = None
if native:
    godot = args.godot or next(Path("/tmp/vortex-parity-godot").glob("*/Godot*linux.x86_64"), None)
    if godot is None:
        cli.error("provide the Godot Mono executable with --godot")
    godot = godot.resolve()
run.mkdir(mode=0o700)

fixture=(repo/'planning/parity/attached-damage/stormkeep-probe.ent').read_bytes()+b'''
{
"classname" "weapon_fireball"
"origin" "1040 240 8"
"spawnflags" "1"
"respawntimestart" "1"
"respawntime" "20"
}
{
"classname" "misc_teleporter_dest"
"targetname" "probe_listener"
"origin" "1070 300 24"
"angle" "135"
}
{
"classname" "weapon_fireball"
"origin" "985 275 8"
"spawnflags" "1"
"respawntimestart" "1"
"respawntime" "20"
}
{
"classname" "misc_teleporter_dest"
"targetname" "probe_second"
"origin" "985 275 24"
"angle" "135"
}
'''
if single_pickup:
    fixture=(repo/'planning/parity/attached-damage/stormkeep-probe.ent').read_bytes()+b'\n{\n"classname" "weapon_fireball"\n"origin" "1040 240 32"\n"respawntimestart" "4"\n}\n'
if native and spawn_shield:
    fixture=fixture.replace(b' 240 8', b' 240 32').replace(b' 275 8', b' 275 32').replace(b'\n"spawnflags" "1"', b'')
archive=repo/'data/maps/stormkeep.pk3'
data=run/'data'
data.mkdir()
with zipfile.ZipFile(archive) as original, zipfile.ZipFile(data/'stormkeep.pk3','w',zipfile.ZIP_DEFLATED) as edited:
    for name in original.namelist():
        if name=='maps/stormkeep.ent':
            continue
        b=original.read(name)
        if name=='maps/stormkeep.bsp':
            offset=len(b)
            b=b[:8]+struct.pack('<ii',offset,len(fixture)+1)+b[16:]+fixture+b'\0'
        edited.writestr(name,b)
if native:
    for d in (repo/'data').glob('*.pk3dir'):
        (data/d.name).symlink_to(d)
    for d in (repo/'data/maps').glob('*.pk3'):
        if d!=archive: (data/d.name).symlink_to(d)
else:
    (data/'base.pk3dir').symlink_to(base)
    (data/'zz-code.pk3dir').symlink_to(stock/'compiled')
    (data/'shared.pk3').symlink_to(repo/'data/maps/shared.pk3')
env=os.environ|dict(LIBGL_ALWAYS_SOFTWARE='1',LP_NUM_THREADS='2',XDG_DATA_HOME=str(run/'xdg'),
    XDG_CONFIG_HOME=str(run/'config'),DBUS_SESSION_BUS_ADDRESS='disabled:',SDL_AUDIODRIVER='pulseaudio',VORTEX_AUDIO_NOIDLEMUTE='1',
    LD_LIBRARY_PATH=str(stock/'sdl/usr/lib/x86_64-linux-gnu'),PULSE_SERVER='unix:'+str(run/'pulse'),
    VORTEX_NO_MASTER_QUERY='1')
children=[]
t0=time.monotonic()
def log(*parts):
    stamp = round(time.monotonic() - t0, 3)
    print(stamp, *parts, flush=True)
    with (run / 'timeline.log').open('a', encoding='utf-8') as timeline:
        print(stamp, *parts, file=timeline)

log('scenario', args.engine, args.phase)
def launch(name,cmd,**kwargs):
    e=env|kwargs.pop('env',{})
    log(name,cmd)
    child=sp.Popen([str(x) for x in cmd],env=e,stdout=(run/(name+'.log')).open('wb'),stderr=sp.STDOUT,**kwargs)
    children.append(child)
    return child
def wait(name,pattern,limit=90):
    end=time.monotonic()+limit
    while time.monotonic()<end:
        if re.search(pattern,(run/(name+'.log')).read_text(errors='replace')): return
        time.sleep(.25)
    raise TimeoutError(f"{name} did not reach {pattern!r}")
port=(28957 if native else 28958)+args.display_offset + (2 if overlap else 0)
cvars=dict(sv_public='0',sv_cheats='1',sv_autopause='0',sv_eventlog='1',sv_eventlog_console='1',g_warmup='0',
    g_start_delay='0',g_spawnshieldtime=str(int(spawn_shield)),g_balance_superweapons_time='10',g_powerups='1',g_pickup_respawntime_superweapon='40' if single_pickup else '5',g_pickup_respawntimejitter_superweapon='0',g_pickup_respawntime_initial_random='0',bot_number='0',net_messagetimeout='150',net_connecttimeout='150')
client=dict(cl_welcome='0',cl_startup_disclaimer='0',cl_maxfps='45',cl_frameprofiler='-1',cl_autopause='0',
    snd_mutewhenidle='0',bgmvolume='0',volume='1',cl_precache_all_weapons='0',cl_allow_uidtracking='0',
    cl_allow_uidranking='0',cl_allow_uid2name='0',cl_netfps='72')
def cv(d):
    return [part for k,v in d.items() for part in (('--cvar',k,v) if native else ('+'+k,v))]
def x(d,*a):
    return sp.check_output(['xdotool',*[str(e) for e in a]],env=env|{'DISPLAY':d},timeout=25,text=True)
roles={}
def start_client(role,display,hosting=False):
    user=run/role
    user.mkdir()
    if native:
        cmd=[godot,'--path',repo,'--data',data,'--no-save-config','--rendering-method','gl_compatibility',
             '--audio-driver','PulseAudio','--resolution',args.resolution]
        cmd+=['--host','stormkeep','--port',str(port)] + cv(cvars) if hosting else ['--connect',f'127.0.0.1:{port}']
        cmd+=cv(client|{'_cl_name':role})
    else:
        cfg=user/'data'
        cfg.mkdir()
        (cfg/'config.cfg').write_text('\n'.join(f'bind {k} "{v}"' for k,v in {
            'F6':'cmd join','F7':'cmd teleporttotarget probe_'+('shooter' if role=='picker' else 'victim'),
            'F12':'cmd teleporttotarget probe_second','F11':'cmd teleporttotarget probe_listener','F8':'cmd give 0 fireball','F9':'cmd give unlimited_superweapons','F10':'cmd give 0 unlimited_superweapons'}.items()))
        cmd=[stock/'xonotic-linux64-sdl','-xonotic','-basedir',run,'-userdir',user,'-nojoy','-window','-width',width,'-height',height,
             *cv(client|{'_cl_name':role}),'+connect',f'127.0.0.1:{port}']
    proc=launch(role,cmd,env={'DISPLAY':display,'PULSE_SINK':role,'VORTEX_USERDIR':str(user)},stdin=sp.PIPE)
    roles[role]=(display,proc)
def focus(role):
    d,proc=roles[role]
    w=x(d,'search','--sync','--onlyvisible','--pid',proc.pid).splitlines()[0]
    x(d,'windowfocus',w)
    return d,w
def key(role,k):
    log(role,k)
    d,_=focus(role)
    x(d,'keydown',k)
    time.sleep(.22)
    x(d,'keyup',k)
def command(role,cmd):
    log(role,cmd)
    d,_=focus(role)
    x(d,'key','grave')
    time.sleep(.40)
    x(d,'type','--clearmodifiers','--delay','18',cmd)
    x(d,'key','Return')
    time.sleep(.3)
    x(d,'key','grave')
def wait_stock(role):
    try:
        wait(role, f"{role}.* connected", limit=45)
    except TimeoutError:
        command(role, f"connect 127.0.0.1:{port}")
        wait(role, f"{role}.* connected", limit=45)

def shot(name):
    d,w=focus('picker')
    sp.run(['import','-window',w,str(run/(name+'.png'))],env=env|{'DISPLAY':d},check=True)
try:
    (run/'runtime').mkdir(mode=0o700)
    env['XDG_RUNTIME_DIR']=str(run/'runtime')
    launch('pulse',['pulseaudio','-n','--daemonize=no','--exit-idle-time=-1','--use-pid-file=no',
        '-L','module-native-protocol-unix socket='+str(run/'pulse')+' auth-anonymous=1',
        '-L','module-null-sink sink_name=picker rate=48000',
        '-L','module-null-sink sink_name=peer rate=48000'])
    for i in range(100):
        if (run/'pulse').exists(): break
        time.sleep(.1)
    else:
        raise RuntimeError('private PulseAudio socket did not start')
    displays=[]
    for n in range(2):
        num=(177 if native else 175)+n+args.display_offset
        launch(f'x{n}',['Xvfb',f':{num}','-screen','0','640x448x24','-nolisten','tcp'])
        displays.append(f':{num}')
    time.sleep(.4)
    if not native:
        launch('server',[stock/'xonotic-linux64-sdl','-xonotic','-basedir',run,'-userdir',run/'server',
            '-dedicated','-ip','127.0.0.1','-port',str(port),*cv(cvars),'+map','stormkeep'],stdin=sp.PIPE)
        wait('server','Server listening')
    start_client('picker',displays[0],hosting=native)
    if native: wait('picker',f'UDP {port}')
    if not native:
        focus('picker')
        wait_stock('picker')
        time.sleep(2)
    start_client('peer',displays[1])
    for role in roles:
        focus(role)
        if native:
            wait(role, 'client loaded map' if role == 'peer' else f'UDP {port}')
        else:
            wait_stock(role)
    time.sleep(7)
    for role in roles:
        if native:
            command(role,'cmd join')
        else:
            key(role,'F6')
            key(role,'F6')
    time.sleep(15 if single_pickup else 5)
    for role in roles:
        with (run/(role+'.raw')).open('wb') as out:
            rec=sp.Popen(['parec','--device='+role+'.monitor','--raw','--rate=48000','--channels=2','--format=s16le'],env=env,stdout=out)
            children.append(rec)
    log('audio recording')
    if native: command('peer','cmd teleporttotarget probe_victim')
    else: key('peer','F7')
    time.sleep(2)
    shot('before')
    if native: command('picker','cmd teleporttotarget probe_shooter')
    else: key('picker','F7')
    time.sleep(2)
    shot('pickup')
    if native and overlap:
        command('picker','cmd give fireball')
        time.sleep(1)
        shot('given')
    if overlap:
        if native: command('picker','cmd give unlimited_superweapons')
        else: key('picker','F9')
    if not single_pickup:
        if native: command('picker','cmd teleporttotarget probe_listener')
        else: key('picker','F11')
    time.sleep(14)
    shot('after')
    if overlap:
        if native: command('picker','cmd give 0 unlimited_superweapons')
        else: key('picker','F10')
        time.sleep(2)
        shot('after-release')
    if not overlap and not single_pickup:
        if native: command('picker','cmd teleporttotarget probe_second')
        else: key('picker','F12')
        time.sleep(2)
        shot('pickup-again')
        if native:
            command('picker','cmd give 16 superweapons fireball')
            time.sleep(2)
        if native: command('picker','cmd give 0 fireball')
        else: key('picker','F8')
        time.sleep(2)
        shot('after-drop')
    time.sleep(2)
    log('done')
finally:
    for child in reversed(children): child.terminate()
    for child in reversed(children):
        try: child.wait(timeout=4)
        except sp.TimeoutExpired:
            child.kill()
            child.wait()
