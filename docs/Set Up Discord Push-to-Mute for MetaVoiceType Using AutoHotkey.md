# Set Up Discord Push-to-Mute for MetaVoiceType Using AutoHotkey

## Goal

This setup lets **MetaVoiceType** hold a key while recording so **Discord temporarily mutes your microphone** during speech recognition or paste workflows.

A good default choice is **F24**, because it usually does not conflict with normal shortcuts like **Ctrl+V**.

---

## How it works

The idea is simple:

1. **Discord** is configured so that **holding F24 = Push to Mute**
2. **MetaVoiceType** is configured so that **while recording, it holds F24**
3. While MetaVoiceType is recording, Discord sees F24 being held and **keeps your mic muted**
4. When recording ends, MetaVoiceType releases F24 and Discord **unmutes again**

So the important part is **not AutoHotkey itself**. AutoHotkey is just a convenient way to help you **enter or simulate F24**, since most keyboards do not have a physical F24 key.

That means people can also do this another way if they want, as long as they follow the same core idea:

- pick a key that will not interfere with other shortcuts
- bind that key in **Discord** as **Push to Mute**
- make **MetaVoiceType** hold that same key while recording

For example, instead of AutoHotkey, someone could use:

- a macro keyboard
- a mouse button remapper
- another automation tool
- a custom script or app
- any other method that can send and hold **F24** or another unused key

---

## 1. Install AutoHotkey

Download and install **AutoHotkey v2** from:

[https://www.autohotkey.com/](https://www.autohotkey.com/)

---

## 2. Run the included F24 mapping script

MetaVoiceType includes the AutoHotkey script:

`CapsLockF24.ahk`

Run this file by double-clicking it.

While the script is running, **Caps Lock acts as F24**. You should see the AutoHotkey icon in your system tray.

If you want to know what the script does, it is just this:

```ahk
CapsLock::F24
```

That line means: when you press **Caps Lock**, AutoHotkey sends **F24** instead.

---

## 3. Set up Discord Push to Mute

In Discord:

1. Open **User Settings**
2. Go to **Keybinds**
3. Click **Add a Keybind**
4. Set **Keybind Action** to **Push to Mute**
5. Click **Record Keybind**
6. Press **Caps Lock**

Because `CapsLockF24.ahk` is running, Discord should record **F24**.

After this, Discord should show:

- **Push to Mute**
- **F24**

---

## 4. Set up MetaVoiceType

In **MetaVoiceType** general settings, under recording event shortcuts:

- **When recording starts**: leave unset unless you want a start action
- **When recording stops**: leave unset unless you want a stop action
- **Hold while recording**: type **F24** directly (or use **Record**)

This way, MetaVoiceType will **hold F24 for the entire recording**, and Discord will stay muted for that whole time.

---

MetaVoiceType saves this setting automatically. AutoHotkey is still needed to present **F24** to Discord's keybind recorder when your physical keyboard does not have that key.

---

## 5. Close AutoHotkey if you want

Once Discord has recorded **F24**, you can usually close `CapsLockF24.ahk` if you only needed it to make entering F24 easier.

You can run `CapsLockF24.ahk` again later if you ever need to re-record the shortcut.

To close it:

1. Right-click the AutoHotkey tray icon
2. Click **Exit**

---

## Why F24?

**F24** is a good choice because:

- it almost never conflicts with app shortcuts
- it does not interfere with **paste**
- it avoids using **Ctrl**, **Alt**, or **Shift**
- it works well as a dedicated automation key
- most applications ignore it unless you explicitly bind it

---

## Can I use a different key?

Yes. The important thing is that **Discord and MetaVoiceType use the same key**.

If you want to use something other than F24, you can. But in general, avoid keys that may interfere with normal use, such as:

- **Ctrl**
- **Alt**
- **Shift**
- common letter keys
- common function shortcuts already used by apps

That is why **F24** is recommended as the default.

---

## Troubleshooting

### Discord does not record F24

- Make sure `CapsLockF24.ahk` is running
- Click **Record Keybind** again
- Press **Caps Lock** once more

### Push to Mute does not work while recording

- Confirm Discord's keybind action is **Push to Mute**, not Toggle Mute
- Confirm MetaVoiceType **Hold while recording** is set to **F24**
- Confirm MetaVoiceType shows **Saved automatically** after you type or record the shortcut

### Caps Lock still acts like Caps Lock

- Make sure `CapsLockF24.ahk` is actually running
- Check for the AutoHotkey icon in the system tray

### I want to do it without AutoHotkey

That is fine. Just make sure:

- Discord is bound to **Push to Mute**
- the key chosen is the same key MetaVoiceType holds while recording
- the key does not interfere with other shortcuts

AutoHotkey is just the easiest way to get a usable **F24** key on a normal keyboard.

---

## Summary

- Run the included `CapsLockF24.ahk` file
- In **Discord**, set **Push to Mute** to **F24** by pressing Caps Lock
- In **MetaVoiceType**, set **Hold while recording** to **F24**
- MetaVoiceType saves the shortcut automatically

Conceptually, the setup works because **MetaVoiceType holds a dedicated key while recording**, and **Discord treats that held key as Push to Mute**. AutoHotkey is simply one convenient way to make **F24** available.
