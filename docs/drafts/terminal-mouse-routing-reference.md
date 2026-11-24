# Terminal mouse routing in Tera Term and RLogin

Reviewed 2026-11-24 for the TabTty specification discussion. Findings come
from the manuals and tagged sources for Tera Term 5.7.1 and RLogin 2.32.0.
The review used source inspection only. [Tera Term release](https://github.com/TeraTermProject/teraterm/releases/tag/v5.7.1),
[RLogin release](https://github.com/kmiya-culti/RLogin/releases/tag/2.32.0).

## Selection and mouse buttons

| Input in the terminal text area | Tera Term 5.7.1 | RLogin 2.32.0 |
| --- | --- | --- |
| Left drag with tracking off | Selects characters and copies the selection to the Windows clipboard by default. Shift+click extends a selection. [Mouse gestures](https://teratermproject.github.io/manual/5/en/usage/mouse.html), [setting defaults](https://teratermproject.github.io/manual/5/en/setup/teraterm-ini.html). | Selects characters. Copy on selection is optional. Shift during a local drag selects whole lines and Ctrl selects a rectangle. [Clipboard settings](https://kmiya-culti.github.io/RLogin/#CLIPOPT), [selection examples](https://kmiya-culti.github.io/RLogin/faq.html#LCLICK). |
| Click or drag with tracking active | Sends the events the requested mode supports. Ctrl bypasses tracking by default. Shift can be part of the remote report. [Tracking settings](https://teratermproject.github.io/manual/5/en/menu/setup-additional-ctrlseq.html), [report modifiers](https://github.com/TeraTermProject/teraterm/blob/v5.7.1/teraterm/teraterm/vtterm.c#L5632-L5667). | Both plain and Shift+left-click go to the application. Shift is a report modifier in modes that encode it. The Edit menu's **Disable mouse events** temporarily restores local selection. [Left-button handler](https://github.com/kmiya-culti/RLogin/blob/2.32.0/RLogin/RLoginView.cpp#L2933-L2957), [modifier encoding](https://github.com/kmiya-culti/RLogin/blob/2.32.0/RLogin/TextRam.cpp#L9972-L9983), [Edit menu](https://kmiya-culti.github.io/RLogin/#MENU). |
| Right and middle buttons | With local handling, right-click pastes by default. Middle-click paste is disabled by default. [Mouse gestures](https://teratermproject.github.io/manual/5/en/usage/mouse.html), [setting defaults](https://teratermproject.github.io/manual/5/en/setup/teraterm-ini.html). | Right-click opens the local Edit menu by default. Active tracking routes it to the application's configured button number. Middle-click uses a key mapping. [Initial options](https://github.com/kmiya-culti/RLogin/blob/2.32.0/RLogin/TextRam.cpp#L3025-L3044), [right button](https://github.com/kmiya-culti/RLogin/blob/2.32.0/RLogin/RLoginView.cpp#L3409-L3434), [middle button](https://github.com/kmiya-culti/RLogin/blob/2.32.0/RLogin/RLoginView.cpp#L3114-L3119). |

## Wheel input

With wheel-capable tracking, a remote wheel report identifies the direction
and pointer position. With tracking off, an emulator may send Up/Down cursor
keys instead. These carry no pointer position and the receiving application
interprets them as keyboard input. Local scrolling changes the terminal view.
[XTerm wheel protocol](https://invisible-island.net/xterm/ctlseqs/ctlseqs.html#h3-Wheel-mice),
[tmux wheel routing examples](https://github.com/tmux/tmux/wiki/Recipes#send-up-and-down-keys-for-the-mouse-wheel).

- **Tera Term** tries a mouse report first. If wheel-to-cursor translation is
  active in application cursor mode, the fallback sends Up/Down. Otherwise the
  terminal scrolls locally. By default Ctrl suppresses both reporting and
  translation. [Wheel handler](https://github.com/TeraTermProject/teraterm/blob/v5.7.1/teraterm/teraterm/vtwin.cpp#L2583-L2620),
  [wheel settings](https://teratermproject.github.io/manual/5/en/setup/teraterm-misc.html#mousewheel).
- **RLogin** handles Shift+wheel across the terminal windows in the same
  RLogin instance. Each receives the gesture with Shift removed. Ctrl+wheel
  adjusts the local font size by default. An ordinary wheel gesture goes to
  the mouse reporter if tracking is active. Otherwise, application cursor
  mode can map it to Up/Down, and private mode 7786 can also request that
  mapping. The fallback scrolls local history. [Wheel handler](https://github.com/kmiya-culti/RLogin/blob/2.32.0/RLogin/RLoginView.cpp#L2835-L2898),
  [mode 7786](https://github.com/kmiya-culti/RLogin/blob/2.32.0/RLogin/TextRam.cpp#L8020-L8022),
  [window dispatch](https://github.com/kmiya-culti/RLogin/blob/2.32.0/RLogin/RLogin.cpp#L2945-L2973),
  [wheel settings](https://kmiya-culti.github.io/RLogin/#MOUSEOPT).

## Options considered for TabTty

The earlier Q73 assigned plain gestures to the application while tracking
is active, with Shift selecting local handling. Giving plain left-drag to
local selection and Shift+drag to the application would reverse this priority.
Consuming Shift for routing would let Vim or tmux receive an ordinary drag.
Forwarding the Shift bit would give the application a modified drag.
[Q73](terminal-interaction-issue.md#confirmed-remote-mouse-routing-q73-accepted),
[TabTty mouse reports](../specification/terminal-and-encodings.md#keyboard-and-mouse).

For a wheel gesture on the alternate screen, the open choices concern the
reported event and its fallback. With wheel-capable tracking, TabTty could
send a wheel report with either Shift state. Without it, Up/Down would be a
separate keyboard mapping. XTerm provides a direct precedent for this second
choice through its alternate-scroll mode. Vim and tmux may act differently
on a wheel report and a cursor key. [XTerm wheel protocol](https://invisible-island.net/xterm/ctlseqs/ctlseqs.html#h3-Wheel-mice),
[tmux wheel routing examples](https://github.com/tmux/tmux/wiki/Recipes#send-up-and-down-keys-for-the-mouse-wheel).

The subsequent decision uses a per-connection menu action in TabTty to switch
clicks, drags, and the wheel together. [Current mouse routing contract](../specification/terminal-and-encodings.md#keyboard-and-mouse).
