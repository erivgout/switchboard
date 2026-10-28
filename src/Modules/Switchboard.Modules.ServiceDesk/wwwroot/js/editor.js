// Lightweight rich-text editor (contenteditable) and file uploads for the service desk.
// The server sanitizes everything; this only produces simple markup: bold, italic, lists, links.
//
// Markup:
//   <div data-sd-editor>
//     <div data-sd-cmd="bold">…toolbar…</div>
//     <div contenteditable data-sd-editor-area data-placeholder="…"></div>
//     <input type="hidden" name="…" data-sd-editor-value>
//   </div>
//   <input type="file" data-sd-upload> + <div data-sd-upload-list data-sd-upload-name="AttachmentIds">
(function () {
  "use strict";

  var doc = document;
  var api = (window.sdEditor = window.sdEditor || {});
  var ranges = new WeakMap();

  function isEmpty(area) {
    return area.textContent.replace(/​/g, "").trim() === "" && !area.querySelector("img");
  }

  function sync(wrap) {
    var area = wrap.querySelector("[data-sd-editor-area]");
    var hidden = wrap.querySelector("[data-sd-editor-value]");
    if (!area || !hidden) return;
    hidden.value = isEmpty(area) ? "" : area.innerHTML;
    area.classList.toggle("is-empty", isEmpty(area));
    wrap.dispatchEvent(new CustomEvent("sd:editor-change", { bubbles: true }));
  }

  function saveRange(area) {
    var sel = window.getSelection();
    if (sel && sel.rangeCount > 0 && area.contains(sel.getRangeAt(0).commonAncestorContainer)) {
      ranges.set(area, sel.getRangeAt(0).cloneRange());
    }
  }

  function restoreRange(area) {
    area.focus();
    var range = ranges.get(area);
    var sel = window.getSelection();
    if (!sel) return;
    sel.removeAllRanges();
    if (range && area.contains(range.commonAncestorContainer)) {
      sel.addRange(range);
    } else {
      var end = doc.createRange();
      end.selectNodeContents(area);
      end.collapse(false);
      sel.addRange(end);
    }
  }

  function link(area) {
    saveRange(area);
    var url = window.prompt("Link address", "https://");
    if (!url || url === "https://") return;
    url = url.trim();
    if (!/^(https?:\/\/|mailto:)/i.test(url)) url = "https://" + url;
    restoreRange(area);
    var sel = window.getSelection();
    if (sel && sel.isCollapsed) {
      doc.execCommand("insertText", false, url);
      var r = sel.getRangeAt(0);
      r.setStart(r.endContainer, Math.max(0, r.endOffset - url.length));
      sel.removeAllRanges();
      sel.addRange(r);
    }
    doc.execCommand("createLink", false, url);
  }

  function initEditor(wrap) {
    if (wrap.hasAttribute("data-sd-editor-ready")) return;
    wrap.setAttribute("data-sd-editor-ready", "");
    var area = wrap.querySelector("[data-sd-editor-area]");
    if (!area) return;

    area.addEventListener("input", function () { sync(wrap); });
    area.addEventListener("keyup", function () { saveRange(area); });
    area.addEventListener("mouseup", function () { saveRange(area); });
    area.addEventListener("blur", function () { saveRange(area); });

    area.addEventListener("keydown", function (e) {
      if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === "k") { e.preventDefault(); link(area); sync(wrap); }
    });

    // Paste as plain text: pasted markup from other apps is noisy and gets sanitized away anyway.
    area.addEventListener("paste", function (e) {
      var data = e.clipboardData;
      if (!data) return;
      if (data.files && data.files.length > 0) {
        e.preventDefault();
        upload(wrap.closest("form") || doc, data.files);
        return;
      }
      e.preventDefault();
      doc.execCommand("insertText", false, data.getData("text/plain"));
    });

    wrap.querySelectorAll("[data-sd-cmd]").forEach(function (btn) {
      btn.addEventListener("mousedown", function (e) { e.preventDefault(); });
      btn.addEventListener("click", function () {
        var cmd = btn.getAttribute("data-sd-cmd");
        area.focus();
        if (cmd === "createLink") link(area);
        else doc.execCommand(cmd, false, null);
        sync(wrap);
      });
    });

    sync(wrap);
  }

  api.isEmpty = isEmpty;
  api.sync = sync;
  api.area = function (wrap) { return wrap.querySelector("[data-sd-editor-area]"); };

  /** Inserts sanitized-on-save HTML at the last caret position. */
  api.insertHtml = function (wrap, html) {
    var area = wrap.querySelector("[data-sd-editor-area]");
    restoreRange(area);
    if (isEmpty(area)) area.innerHTML = "";
    doc.execCommand("insertHTML", false, html);
    sync(wrap);
  };

  api.setHtml = function (wrap, html) {
    var area = wrap.querySelector("[data-sd-editor-area]");
    area.innerHTML = html || "";
    sync(wrap);
  };

  api.getHtml = function (wrap) {
    var area = wrap.querySelector("[data-sd-editor-area]");
    return isEmpty(area) ? "" : area.innerHTML;
  };

  // ---------------------------------------------------------------- uploads (POST /uploads)

  function uploadList(scope) {
    return scope.querySelector("[data-sd-upload-list]");
  }

  function formatSize(bytes) {
    return bytes < 1024 * 1024 ? Math.max(1, Math.round(bytes / 1024)) + " KB" : (bytes / (1024 * 1024)).toFixed(1) + " MB";
  }

  function upload(scope, files) {
    var list = uploadList(scope);
    if (!list) return;
    var name = list.getAttribute("data-sd-upload-name") || "AttachmentIds";
    Array.prototype.forEach.call(files, function (file) {
      var chip = doc.createElement("span");
      chip.className = "sd-upload is-uploading";
      chip.setAttribute("data-sd-upload-item", "");
      var icon = doc.createElement("i");
      icon.className = "bi bi-paperclip";
      icon.setAttribute("aria-hidden", "true");
      var label = doc.createElement("span");
      label.textContent = file.name + ", uploading…";
      chip.append(icon, label);
      list.appendChild(chip);

      var body = new FormData();
      body.append("file", file, file.name);
      sb.fetch("/uploads", { method: "POST", body: body })
        .then(function (r) {
          return r.json().catch(function () { return {}; }).then(function (data) {
            if (!r.ok) throw new Error(data.error || "That file couldn't be uploaded.");
            return data;
          });
        })
        .then(function (data) {
          chip.classList.remove("is-uploading");
          label.textContent = data.fileName + " (" + formatSize(data.size) + ")";
          var hidden = doc.createElement("input");
          hidden.type = "hidden";
          hidden.name = name;
          hidden.value = data.id;
          var remove = doc.createElement("button");
          remove.type = "button";
          remove.className = "btn btn-ghost btn-icon btn-sm";
          remove.setAttribute("data-sd-remove-upload", "");
          remove.setAttribute("aria-label", "Remove " + data.fileName);
          remove.innerHTML = '<i class="bi bi-x-lg" aria-hidden="true"></i>';
          chip.append(hidden, remove);
          chip.dispatchEvent(new CustomEvent("sd:uploaded", { bubbles: true }));
        })
        .catch(function (err) {
          chip.remove();
          sb.toast(file.name + ": " + err.message, "error");
        });
    });
  }

  api.upload = upload;

  doc.addEventListener("change", function (e) {
    if (!e.target.matches("input[type=file][data-sd-upload]")) return;
    var scope = e.target.closest("form") || doc;
    upload(scope, e.target.files);
    e.target.value = "";
  });

  doc.addEventListener("click", function (e) {
    var remove = e.target.closest("[data-sd-remove-upload]");
    if (!remove) return;
    var chip = remove.closest("[data-sd-upload-item]");
    if (chip) chip.remove();
  });

  function init(root) {
    root.querySelectorAll("[data-sd-editor]").forEach(initEditor);
  }

  doc.addEventListener("DOMContentLoaded", function () { init(doc); });
  doc.addEventListener("htmx:afterSettle", function (e) { init(e.target); });

  // Forms that post an editor's HTML make sure the hidden value is current.
  doc.addEventListener("submit", function (e) {
    e.target.querySelectorAll && e.target.querySelectorAll("[data-sd-editor]").forEach(sync);
  }, true);
  doc.addEventListener("htmx:configRequest", function (e) {
    var form = e.detail.elt && e.detail.elt.closest && e.detail.elt.closest("form");
    if (!form) return;
    form.querySelectorAll("[data-sd-editor]").forEach(function (wrap) {
      sync(wrap);
      var hidden = wrap.querySelector("[data-sd-editor-value]");
      if (hidden && hidden.name) e.detail.parameters[hidden.name] = hidden.value;
    });
  });
})();
