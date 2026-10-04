"""Private stdin/stdout bridge: JSON commands, length-prefixed JSON + BGRA responses.

Uses the desktop's consent dialog; no screen content is written to disk.
"""
import json
import os
import struct
import sys
import threading
import uuid

import gi
gi.require_version('Gst', '1.0')
from gi.repository import Gio, GLib, Gst
try:
    gi.require_version('GstVideo', '1.0')
    from gi.repository import GstVideo
except (ValueError, ImportError):
    GstVideo = None

BUS_NAME = 'org.freedesktop.portal.Desktop'
PATH = '/org/freedesktop/portal/desktop'
SC = 'org.freedesktop.portal.ScreenCast'
RD = 'org.freedesktop.portal.RemoteDesktop'
bus = Gio.bus_get_sync(Gio.BusType.SESSION, None)
Gst.init(None)
loop = GLib.MainLoop()
threading.Thread(target=loop.run, daemon=True).start()
session = None
pipelines = {}
last_frames = {}
remote = None
held_keys = set()
held_buttons = set()
input_enabled = False
session_closed = threading.Event()


def call(interface, method, signature, args, path=PATH):
    return bus.call_sync(BUS_NAME, path, interface, method,
                         GLib.Variant(signature, args), None,
                         Gio.DBusCallFlags.NONE, 10000, None)


def request(interface, method, signature, args, options):
    token = 'u' + uuid.uuid4().hex
    options['handle_token'] = GLib.Variant('s', token)
    sender = bus.get_unique_name()[1:].replace('.', '_')
    request_path = '/org/freedesktop/portal/desktop/request/' + sender + '/' + token
    done = threading.Event()
    result = []

    def response(connection, sender, path, iface, signal, params):
        result.extend(params.unpack())
        done.set()

    subscription = bus.signal_subscribe(BUS_NAME, 'org.freedesktop.portal.Request',
                                       'Response', request_path, None,
                                       Gio.DBusSignalFlags.NONE, response)
    try:
        call(interface, method, signature, (*args, options))
        if not done.wait(120):
            call('org.freedesktop.portal.Request', 'Close', '()', (), request_path)
            raise RuntimeError('桌面授权超时，请重新开启被控。')
        if result[0] != 0:
            raise RuntimeError('桌面共享授权已取消或被拒绝。')
        return result[1]
    finally:
        bus.signal_unsubscribe(subscription)


def property_value(interface, name):
    return call('org.freedesktop.DBus.Properties', 'Get', '(ss)', (interface, name)).unpack()[0]


def reply(metadata, pixels=b''):
    data = json.dumps(metadata, ensure_ascii=False).encode('utf-8')
    sys.stdout.buffer.write(struct.pack('<I', len(data)))
    sys.stdout.buffer.write(data)
    sys.stdout.buffer.write(pixels)
    sys.stdout.buffer.flush()


def notify(method, signature, *values):
    if not input_enabled:
        raise RuntimeError('键鼠控制未授权。')
    call(RD, method, '(oa{sv}' + signature + ')', (session, {}, *values))


def start(enable_input):
    global session, input_enabled, remote
    interface = RD if enable_input else SC
    created = request(interface, 'CreateSession', '(a{sv})', (), {
        'session_handle_token': GLib.Variant('s', 'u' + uuid.uuid4().hex)})
    session = created['session_handle']
    bus.signal_subscribe(BUS_NAME, 'org.freedesktop.portal.Session', 'Closed', session,
                         None, Gio.DBusSignalFlags.NONE,
                         lambda *args: session_closed.set())
    if enable_input:
        request(RD, 'SelectDevices', '(oa{sv})', (session,), {'types': GLib.Variant('u', 3)})
    request(SC, 'SelectSources', '(oa{sv})', (session,), {
        'types': GLib.Variant('u', 1), 'multiple': GLib.Variant('b', True),
        'cursor_mode': GLib.Variant('u', 2)})
    result = request(interface, 'Start', '(osa{sv})', (session, ''), {})
    if enable_input and result.get('devices', 0) & 3 != 3:
        raise RuntimeError('未授予完整键鼠权限，请允许键盘和鼠标后重试。')
    input_enabled = enable_input
    streams = result.get('streams', [])
    if not 1 <= len(streams) <= 5:
        raise RuntimeError('请选择 1 至 5 个显示器。')
    returned, fds = bus.call_with_unix_fd_list_sync(
        BUS_NAME, PATH, SC, 'OpenPipeWireRemote', GLib.Variant('(oa{sv})', (session, {})),
        GLib.VariantType.new('(h)'), Gio.DBusCallFlags.NONE, 10000, None, None)
    remote = fds.get(returned.unpack()[0])
    outputs = []
    for node, properties in streams:
        pipeline = Gst.Pipeline.new(None)
        source = Gst.ElementFactory.make('pipewiresrc')
        convert = Gst.ElementFactory.make('videoconvert')
        sink = Gst.ElementFactory.make('appsink')
        if any(element is None for element in (source, convert, sink)):
            raise RuntimeError('缺少 GStreamer PipeWire、videoconvert 或 appsink 组件。')
        source.set_property('fd', remote)
        source.set_property('path', str(node))
        source.set_property('do-timestamp', True)
        sink.set_property('caps', Gst.Caps.from_string('video/x-raw,format=BGRA'))
        sink.set_property('max-buffers', 1)
        sink.set_property('drop', True)
        sink.set_property('sync', False)
        for element in (source, convert, sink):
            pipeline.add(element)
        if not source.link(convert) or not convert.link(sink):
            raise RuntimeError('无法连接 PipeWire 视频管线。')
        pipelines[node] = (pipeline, sink, properties)
        if pipeline.set_state(Gst.State.PLAYING) == Gst.StateChangeReturn.FAILURE:
            raise RuntimeError('无法启动 PipeWire 视频管线。')
        outputs.append(node)
    return {'outputs': outputs}


def capture(node):
    pipeline, sink, properties = pipelines[node]
    # PipeWire may only emit a new buffer when desktop content changes. Never
    # block the shared command pipe waiting for damage after the first frame.
    cached = last_frames.get(node)
    sample = sink.emit('try-pull-sample', 0 if cached else 10 * Gst.SECOND)
    if sample is None:
        if cached:
            reply(*cached)
            return
        raise RuntimeError('屏幕共享已结束或无法获取画面。')
    caps = sample.get_caps().get_structure(0)
    width, height = caps.get_value('width'), caps.get_value('height')
    if not 0 < width <= 16384 or not 0 < height <= 16384 or width * height * 4 > 268435456:
        raise RuntimeError('屏幕尺寸超过支持范围。')
    buffer = sample.get_buffer()
    ok, mapped = buffer.map(Gst.MapFlags.READ)
    if not ok:
        raise RuntimeError('无法读取视频缓冲区。')
    try:
        if GstVideo:
            video_info = GstVideo.VideoInfo.new_from_caps(sample.get_caps())
            video_meta = GstVideo.buffer_get_video_meta(buffer)
            stride = video_meta.stride[0] if video_meta else video_info.stride[0]
            offset = video_meta.offset[0] if video_meta else video_info.offset[0]
        else:
            # videoconvert negotiates a single BGRA plane; some distributions
            # ship Gst without the optional GstVideo Python typelib.
            if len(mapped.data) % height:
                raise RuntimeError('无效的视频缓冲区。')
            stride, offset = len(mapped.data) // height, 0
        if stride < width * 4 or stride * height > 268435456:
            raise RuntimeError('无效的视频行跨度。')
        pixels = bytearray(mapped.data[offset:offset + stride * height])
        if len(pixels) != stride * height:
            raise RuntimeError('无效的视频缓冲区。')
        metadata = {'width': width, 'height': height, 'stride': stride, 'bytes': len(pixels)}
        if cached:
            cached[1][:] = b'\0' * len(cached[1])
        last_frames[node] = (metadata, pixels)
        reply(metadata, pixels)
    finally:
        buffer.unmap(mapped)


def command(data):
    op = data['op']
    if session_closed.is_set():
        reply({'closed': True})
        return
    if op == 'health':
        reply({})
        return
    if op == 'capture':
        capture(data['output'])
        return
    if op == 'key':
        code, down = data['code'], data['down']
        notify('NotifyKeyboardKeycode', 'iu', code, int(down))
        (held_keys.add if down else held_keys.discard)(code)
    elif op == 'text':
        text = data['text']
        if not isinstance(text, str) or len(text.encode('utf-8')) > 4096:
            raise RuntimeError('无效的输入文字。')
        for char in text.replace('\r\n', '\n'):
            value = ord(char)
            if (value < 32 or 127 <= value < 160) and char not in '\r\n\t':
                raise RuntimeError('不支持的控制字符。')
            symbol = 0xff0d if char in '\r\n' else 0xff09 if char == '\t' else value if value <= 255 else 0x01000000 | value
            try:
                notify('NotifyKeyboardKeysym', 'iu', symbol, 1)
            finally:
                notify('NotifyKeyboardKeysym', 'iu', symbol, 0)
    elif op == 'button':
        code, down = data['code'], data['down']
        notify('NotifyPointerButton', 'iu', code, int(down))
        (held_buttons.add if down else held_buttons.discard)(code)
    elif op == 'move':
        node = data['output']
        properties = pipelines[node][2]
        size = properties.get('logical_size', properties.get('size'))
        if not size:
            raise RuntimeError('桌面未提供屏幕逻辑尺寸。')
        x, y = data['x'], data['y']
        if not 0 <= x <= 1 or not 0 <= y <= 1:
            raise RuntimeError('无效的指针坐标。')
        notify('NotifyPointerMotionAbsolute', 'udd', node,
               min(x * size[0], size[0] - 1), min(y * size[1], size[1] - 1))
    elif op == 'scroll':
        notify('NotifyPointerAxis', 'dd', data['x'], -data['y'])
    elif op == 'release':
        for code in list(held_keys):
            notify('NotifyKeyboardKeycode', 'iu', code, 0)
        for code in list(held_buttons):
            notify('NotifyPointerButton', 'iu', code, 0)
        held_keys.clear()
        held_buttons.clear()
    else:
        raise RuntimeError('未知桌面指令。')
    reply({})


try:
    if '--probe' in sys.argv:
        if property_value(SC, 'AvailableSourceTypes') & 1 == 0:
            raise RuntimeError('桌面 Portal 不支持显示器共享。')
        for name in ('pipewiresrc', 'videoconvert', 'appsink'):
            if Gst.ElementFactory.find(name) is None:
                raise RuntimeError('缺少 GStreamer 组件：' + name)
        try:
            available_input = property_value(RD, 'AvailableDeviceTypes') & 3 == 3
        except GLib.Error:
            available_input = False
        reply({'input': available_input})
    else:
        reply(start('--input' in sys.argv))
        for line in sys.stdin:
            try:
                command(json.loads(line))
            except Exception as error:
                reply({'error': str(error)})
except Exception as error:
    reply({'error': str(error)})
finally:
    if session:
        try:
            if input_enabled:
                for code in held_keys:
                    notify('NotifyKeyboardKeycode', 'iu', code, 0)
                for code in held_buttons:
                    notify('NotifyPointerButton', 'iu', code, 0)
            call('org.freedesktop.portal.Session', 'Close', '()', (), session)
        except Exception:
            pass
    for pipeline, sink, properties in pipelines.values():
        pipeline.set_state(Gst.State.NULL)
    for metadata, pixels in last_frames.values():
        pixels[:] = b'\0' * len(pixels)
    last_frames.clear()
    if remote is not None:
        os.close(remote)
    loop.quit()
