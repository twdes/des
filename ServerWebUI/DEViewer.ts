/// <reference path="libs/jquery.d.ts" />


function loadTemplate(template: JQuery, node: JQuery): string {
  var html = template.html();

  html = html.replace(/\{\{([A-Z0-9]|\/)+\}\}/gim, function (key) {
    var key = key.substring(2, key.length - 2);

    var pos = key.indexOf('/');
    if (pos == -1) {
      if (key === "image") {
        return 'src="' + node.attr(key) + '"'; // do not load {{image}}
      }
      else
        return node.attr(key);
    } else {
      var c = $(key.substring(0, pos), node);
      return c.attr(key.substring(pos + 1));
    }
  });

  return html;
} // loadTemplate


function formatNumber(n: number, digits: number): string {
  return n.toLocaleString(['de'], { minimumFractionDigits: digits, maximumFractionDigits: digits });
} // formatNumber


function formatValue(type: string, format: string, rawValue: string): string {

  if (format == "FILESIZE") {
    var n = parseInt(rawValue);
    if (n < 2048)
      return formatNumber(n, 0) + " bytes";
    else if (n < 2097152)
      return formatNumber(n / 1024, 0) + " KiB";
    else if (n < 1073741824)
      return formatNumber(n / 1048576, 1) + " MiB";
    else
      return formatNumber(n / 1073741824, 1) + " GiB";
  }
  else if (format === "{0:N0}")
    return formatNumber(parseInt(rawValue), 0);
  else if (format === "{0:N1}")
    return formatNumber(parseInt(rawValue), 1);
  else
    return rawValue;
} // formatValue


class DETab {
  private app: DEViewer;
  private viewId: string;
  private visible: boolean;

  private button: JQuery;
  private root: JQuery;

  constructor(app: DEViewer, viewId: string) {
    this.app = app;
    this.viewId = viewId;
    this.visible = true;

    // init button
    this.button = $(['span[vid="', this.viewId, '"]'].join(""), app.TabBarElement).first();
    this.button.click(e => this.app.selectTab(this));

    // init root
    this.root = $('#' + viewId).first();
  } // ctor
    
  public select() {
    this.button.removeClass('tabunselected');
    this.button.addClass('tabselected');

    this.root.css("display", "block");
  } // select

  public unselect() {
    this.button.removeClass('tabselected');
    this.button.addClass('tabunselected');

    this.root.css("display", "none");
  } // unselect

  public reload(url: string) {
  } // reload

  public showTab(visible: boolean) {
    this.visible = visible;
    this.button.css('display', visible ? 'inline' : 'none');
  } // showTab

  get App() { return this.app; }
  get ViewId() { return this.viewId; }
  get ButtonElement() { return this.button; }
  get RootElement() { return this.root; }
  get IsVisible() { return this.visible; }
} // class DETab


class DELogTab extends DETab {
  constructor(app: DEViewer) {
    super(app, "tabLog");
  }

  public reload(url: string) {
    super.reload(url);

    var firstDate: number = null;

    this.RootElement.empty();

    if (!this.IsVisible)
      return;

    this.App.serverGet(url + "?action=listget&id=tw_lines&start=-500", // last 500
      (function (data) {

        var innerHtmlElements = new Array();

        $('items > line', data).get().reverse().forEach(
          (function (element) {

            var line = $(element);
            var lineType = line.attr('typ');
            var lineStamp = new Date(Date.parse(line.attr('stamp')));
            var lineText = line.text().replace(/\</g, '&lt;').replace(/\>/g, '&gt;');

            if (firstDate == null || firstDate != lineStamp.getDate()) { // add seperator
              innerHtmlElements.push(['<tr><td colspan="2" class="logLineHeader">Datum: ', lineStamp.toLocaleDateString(), '</td></tr>']);
              firstDate = lineStamp.getDate();
            }
            innerHtmlElements.push([
              '<tr>',
              '<td class="logLineTime logLineBk', lineType, '">', lineStamp.toLocaleTimeString(), ',', lineStamp.getMilliseconds().toLocaleString('de', { minimumintegerDigits: 3 }), '</td>',
              '<td class="logLineCell"><div class="logLineText logLineTextSingle">', lineText, '</div></td>',
            '</tr>'].join("")
            );
          }).bind(this)
        );

        this.RootElement.html(innerHtmlElements.join(""));

        // aktivate toggle
        $('.logLineTime', this.RootElement).click(
          function () {
            var text = $('.logLineText', $(this).parent());
            text.toggleClass("logLineTextFull");
            text.toggleClass("logLineTextSingle");
          }
        );

        this.RootElement.scrollTop(0);

      }).bind(this));
  } // reload

} // class DELogTab


class DEProperties extends DETab {

  constructor(app: DEViewer) {
    super(app, "tabProperties");
  } // ctor

  public reload(url: string) {

    var rootElement = this.RootElement;

    this.RootElement.empty();

    this.App.serverGet(url + "?action=listget&id=tw_properties",
      (function (data) {

        // get and sort the properties
        var properties = $('items > property', data).toArray();
        properties.sort(
          function (_a, _b) {
            var a = $(_a);
            var b = $(_b);
            var r = a.attr('category').localeCompare(b.attr('category'));
            if (r === 0) {
              r = a.attr('displayname').localeCompare(b.attr('displayname'));
            }
            return r;
          }
        );

        // generate the html
        rootElement.empty();

        var lastCategory: string = null;

        properties.forEach(
          function (cur) {
            var property = $(cur);
            var currentCategory = property.attr('category');

            // create the category
            if (currentCategory !== lastCategory) {
              rootElement.append(['<tr><th colspan="2">', currentCategory, '</th></tr>'].join(""));
              lastCategory = currentCategory;
            }

            var typeName = property.attr('type');
            var formatString = property.attr('format');
            rootElement.append(['<tr><td>', property.attr('displayname'), '</td><td id="', property.attr('name'), '" class="propertyValue" format="', formatString, '" type="', typeName, '">', formatValue(typeName, formatString, property.text()), '</td></tr>'].join(""));
            rootElement.append(['<tr><td colspan="2" class="propertyDescription">', property.attr('description'), ' (<span class="propertyName">', property.attr('name'), '</span>)</td></tr>'].join(""));
          });

      }).bind(this));
  } // reload

  public updateProperty(propId: string, value: string) {
    var td = $('#' + propId, this.RootElement);
    td.html(formatValue(td.attr('type'), td.attr('format'), value));
  } // updateProperty
} // class DEProperties


class DEConfig extends DETab {

  constructor(app: DEViewer) {
    super(app, "tabConfig");
  } // ctor

  public reload(url: string) {

    this.RootElement.empty();

    this.App.serverGet(url + "?action=config",
      (function (data) {
        this.loadElement(this.RootElement, $(':root', data), 0);
      }).bind(this));
  } // reload

  private loadElement(nodeUI: JQuery, nodeData: JQuery, level: number): void {

    nodeUI.append('<div class="configElementBox">');
    var childUI = $('div', nodeUI).last();
    childUI.append('<div class="configHeader">');
    var headerUI = $('div', childUI).last();
    headerUI.append(['<span class="configHeaderTitle">', nodeData.attr('name'), '</span> <span class="configHeaderDoc">', nodeData.attr('documentation'), '</span>'].join(""));
    childUI.append('</div>');
    
    childUI.append('<table>');
    var attrTable = $('table', childUI).last();

    nodeData.children('attribute').each(
      function (index, element) {
        var c = $(element);
        attrTable.append(['<tr><td classe="configAttributeTitle">', c.attr('name'), '</td><td class="', c.attr('isDefault') === 'true' ? 'configAttributeDefault' : 'configAttributeValue', '">', c.text(), '</td></tr>'].join(""));
        attrTable.append(['<tr><td colspan="2" class="configAttributeDocumentation">', c.attr('documentation'), '</td></tr>'].join(""));
      });

    childUI.append('</table>');

    var le = this.loadElement.bind(this);
    
    nodeData.children('element').each(
      function (index, element) {
        le(childUI, $(element), level + 1);
      }
    );

    nodeUI.append('</div>');
  } // loadElement
} // class DEConfig


class DEServerInfo extends DETab {

  constructor(app: DEViewer) {
    super(app, "tabInfo");
  } // ctor

  public reload(url: string) {
    super.reload(url);

    var serverInfoElement = $('#serverInfo', this.RootElement);
    var dumpFilesElement = $('#dumpList', this.RootElement);
    serverInfoElement.empty();
    dumpFilesElement.empty();

    if (!this.IsVisible)
      return;

    // get server info
    this.App.serverGet("?action=serverinfo",
      (function (data) {
        var serverInfo = $(':root', data);

        serverInfoElement.append(loadTemplate($('#serverInfoTemplate'), serverInfo));

        var tmpl = $('#assemblyInfoTemplate');
        $('assembly', data).each(
          function (index, element) {
            serverInfoElement.append(loadTemplate(tmpl, $(element)));
          }
        );
      })
    );

    this.App.serverGet("?action=listget&id=tw_dumpfiles",
      function (data) {
        var dumpAdded = false;
        $('items > dump', data).each(
          function (index, element) {

            var dump = $(element);

            var id = dump.attr("id");
            var created = new Date(Date.parse(dump.attr("created")));
            dumpFilesElement.append([
              '<li><a href="/?action=dumpload&id=', id, '">',
              created.toLocaleString(),
              ' (', formatValue('long', 'FILESIZE', dump.attr('size')), ')</a></li>'
            ].join(""));

            dumpAdded = true;
          }
        );

        $('#dumpListFrame').css('display', dumpAdded ? 'block' : 'none');
      }
    );

  } // reload
} // class DEServerInfo


class DEViewer {
	
	private currentNodeElement: JQuery;
  private currentUriElement: JQuery;
  private currentImageElement: JQuery;
  private refreshActionElement: JQuery;
	private tabsElement: JQuery;

  private onReloading: boolean = false;
  private currentPath: string;
  private currentHost: string;
  private currentUri: string;
  private currentWebSocket: WebSocket;

  private currentTab: DETab;
  private tabs: DETab[];
  private beginRefreshTimer: number = -1;
	
  constructor() {

    this.currentPath = $(location).attr('pathname');
    var lastShlash = this.currentPath.lastIndexOf('/');
    if (lastShlash < this.currentPath.length - 1) {
      this.currentPath = this.currentPath.substring(0, lastShlash + 1);
    }

    this.currentHost = $(location).attr('href');
    lastShlash = this.currentHost.lastIndexOf('/');
    if (lastShlash < this.currentHost.length - 1) {
      this.currentHost = this.currentHost.substring(0, lastShlash + 1);
    }
	} // ctor

	public init() {

		this.currentNodeElement = $('#currentNode');
    this.currentUriElement = $('#currentUri');
    this.currentImageElement = $('#currentImage');
    this.refreshActionElement = $('#refreshAction');
    this.tabsElement = $('#tabs');

    this.tabs = [
      new DELogTab(this),
      new DEProperties(this),
      new DEConfig(this),
      new DEServerInfo(this)
     ];
		
    this.refreshActionElement.click(e => this.beginReloadIndex());
		this.currentNodeElement.change(e => this.beginRefreshUri($(':selected', e.target)));
		
    this.selectTab(this.tabs[0]);

		// initialize view empty
		this.currentNodeElement.empty();
    this.beginReloadIndex();
  } // init

  public serverGet(request: string, fnRet: (data, status?: string, xhr?) => any, fnFinish?: () => any) {

    var url = this.currentPath + request;

    $.ajax({
        type: "GET",
        url: url,
        cache: false
      })
      .done(function (data, status, xhr) {
        try {
          if (fnRet != null)
            fnRet(data, status, xhr);
        }
        finally {
          if (fnFinish != null)
            fnFinish();
        }
      })
      .fail(function (jqXHR, textStatus, errorThrown) {
        if (fnFinish != null)
          fnFinish();
        alert("Befehl konnte nicht vom Server verarbeitet werden.\nFehler: " + errorThrown);
      }
   );
  } // serverGet
  
  public selectTab(tab: DETab) {

    if (this.currentTab != tab) {

      this.currentTab = tab;
      if (this.currentTab != null)
        this.currentTab.select();

      for (var i = 0; i < this.tabs.length; i++) {
        var c = this.tabs[i];
        if (c != this.currentTab)
          c.unselect();
      }
    }
  } // selectTab
  
	private beginReloadIndex() {

		if (this.onReloading)
			return;

    this.onReloading = true;
    this.refreshActionElement.toggleClass("actionButton", false);
		this.currentUriElement.text("Loading...");

    var obj = this;
    this.serverGet('?action=list',
      function (data) {
        obj.updateIndex(data);
      },
      function () {
        obj.onReloading = false;
        setTimeout(() => obj.refreshActionElement.toggleClass("actionButton", true), 100);
      });
  } // beginReloadIndex

  private updateIndexAppend(current: JQuery, url: string, level: string) {

    // get the attributes
    var name = current.attr('name');
    var displayname = current.attr('displayname');
    var icon = current.attr('icon');
    // check if a log exists
    var hasLog = $('list[id="tw_lines"]', current).first().length > 0;

    if (name === 'Main') {
      name = "";
      displayname = "Data Exchange Server";
    }
    else
      url = url + name + '/';
    
    // insert the option
    this.currentNodeElement.append(['<option name="', name, '" uri="', url, '" icon="', icon, '" hasLog="', hasLog, '">', level, displayname, '</option>'].join(''));

    // insert children
    var obj = this;
    level = level + "&nbsp;&nbsp;&nbsp;";
    current.children('item').each(
      function (index, element) {
        obj.updateIndexAppend($(element), url, level);
      }
    );
  } // updateIndexAppend

  private updateIndex(data) {

    this.currentNodeElement.empty();

    // parse the nodes
    this.updateIndexAppend($(':root', data), "", "");

    // select the current node
    var cur = this.currentUri === null ? $([]) : $('option[uri="' + this.currentUri + '"]', this.currentNodeElement).first();

    if (cur.length === 0) {
      this.beginRefreshUri($('option', this.currentNodeElement).first());
    } else {
      cur.prop('selected', 'true');
      this.beginRefreshUri(cur);
    }
  } // updateIndex

	private beginRefreshUri(option: JQuery): void {

    if (this.beginRefreshTimer != -1)
      clearTimeout(this.beginRefreshTimer);

    var refreshAction = this.refreshActionElement;
    var app = this;

    this.beginRefreshTimer = setTimeout(
      (function () {

        // stop listening for events
        this.stopEventListener();

        // set the new node
        this.currentUri = option.attr('uri');
        this.currentUriElement.text(this.currentHost + this.currentUri);
        this.currentImageElement.attr("src", option.attr("icon"));

        this.startEventListener();

        var hasLog = option.attr('hasLog') === "true";

        // reload tabs
        this.tabs[0].showTab(hasLog);
        this.tabs[3].showTab(this.currentUri === "");

        for (var i = 0; i < this.tabs.length; i++) {
          this.tabs[i].reload(this.currentUri);
        }
        

        // reload actions
        $('#actions > span[loaded="true"]').remove();

        this.serverGet(this.currentUri + '?action=list&recursive=false',
          function (data) {

            $('action', data).each(
              function (index, element) {
                var c = $(element);
                refreshAction.before(['<span class="actionButton" loaded="true" actionId="', c.attr('id'), '">', c.attr('displayname'), '</span>'].join(""));
              });
            
            $('#actions > span[loaded="true"]').click(
              function (e) {

                var cmd = $(this);
                cmd.toggleClass('actionButton', false);
                app.serverGet([app.currentUri, '?action=', cmd.attr('actionId')].join(""),
                  function (returnData) {
                    var r = $(":first-child", returnData);
                    if (r.attr("status") == "ok") {
                      var text = r.attr("text");
                      if (text != null)
                        alert(text);
                    }
                    else {
                      alert("Aufruf fehlgeschlagen:\n" + r.attr("text"));
                    }
                  },
                  function () {
                    cmd.toggleClass('actionButton', true);
                  });
              });
          });

      }).bind(this), 200);
  } // beginRefreshUri

  private startEventListener() {
    var wsHost = "ws" + this.currentHost.substring(4);
    
    this.currentWebSocket = new WebSocket(wsHost + this.currentUri, "des_event");
    this.currentWebSocket.onmessage = this.eventListenerMessage.bind(this);
  } // startEventListener

  private eventListenerMessage(ev: MessageEvent) {
    if (ev.type === "message") {
      var d = $(':root', $.parseXML(ev.data));

      //console.log("raw event: " + ev.data);

      // check the path
      if (this.currentUri === d.attr('path').substring(1)) {
        var eventId = d.attr('event');
        if (eventId === 'tw_properties')
          (<DEProperties>this.tabs[1]).updateProperty(d.attr('index'), d.text());
        else if (eventId === 'tw_lines') {
          // todo
        }
      }

    }
  } // eventListenerMessage
  
  private stopEventListener() {
    if (this.currentWebSocket != null)
      this.currentWebSocket.close();
    this.currentWebSocket = null;
  } // startEventListener

  get TabBarElement() { return this.tabsElement; }
} // class DEViewer

var desViewer: DEViewer = new DEViewer();
$(document).ready(e => desViewer.init());