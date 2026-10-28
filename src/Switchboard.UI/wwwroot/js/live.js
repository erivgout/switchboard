// Real-time channel (SignalR /hubs/live). Pages listen with:
//   document.addEventListener("sb:live", e => { if (e.detail.type === "ticket.updated") ... })
// and opt into a ticket's room with sbLive.watchTicket(id).
(function () {
  "use strict";
  if (!window.signalR || !document.querySelector('meta[name="sb-live"]')) return;

  var watched = new Set();
  var connection = new signalR.HubConnectionBuilder()
    .withUrl("/hubs/live")
    .withAutomaticReconnect([0, 2000, 5000, 10000, 30000])
    .build();

  connection.on("message", function (type, payload) {
    document.dispatchEvent(new CustomEvent("sb:live", { detail: { type: type, payload: payload } }));
  });

  connection.onreconnected(function () {
    watched.forEach(function (id) { connection.invoke("WatchTicket", id).catch(function () {}); });
  });

  var ready = connection.start().catch(function () { /* live updates are optional */ });

  window.sbLive = {
    connection: connection,
    watchTicket: function (id) {
      watched.add(id);
      return ready.then(function () { return connection.invoke("WatchTicket", id); }).catch(function () {});
    },
    unwatchTicket: function (id) {
      watched.delete(id);
      return ready.then(function () { return connection.invoke("UnwatchTicket", id); }).catch(function () {});
    },
    /** Tell other viewers you are composing a reply (collision detection). */
    setComposing: function (id, composing) {
      return ready.then(function () { return connection.invoke("SetComposing", id, !!composing); }).catch(function () {});
    }
  };

  // Pages with <body data-sb-watch-ticket="guid"> join that ticket's room automatically.
  document.addEventListener("DOMContentLoaded", function () {
    var id = document.body.getAttribute("data-sb-watch-ticket");
    if (id) window.sbLive.watchTicket(id);
  });
})();
