/* 在地图闭包内运行，导出可追溯到当前页面配置的几何和样式。 */
var pptPage = null, pptOnlyBase = false;
var captureSite = [SITE.lng, SITE.lat];
function pptFeatures() {
  project();
  var out=[], pf=(CFG2.ppt.pages||{})[pptPage]||{}, enabled=pf.enabled||[];
  function add(layer,kind,xy,name,source,extra) {
    if(enabled.indexOf(layer)<0)return;
    out.push(Object.assign({id:layer+'-'+out.length,layer:layer,kind:kind,xy:xy,name:name||'',source:source},extra||{}));
  }
  function meters(a){var b=[],mpp=metersPerPixel();for(var i=0;i<a.length;i+=2)b.push(baseP0.x+a[i]/mpp,baseP0.y-a[i+1]/mpp);return b;}
  function point(lng,lat){var p=map.lngLatToContainer(new AMap.LngLat(lng,lat));return [p.x,p.y];}
  if(enabled.includes('project'))add('project','point',[baseP0.x,baseP0.y],'基地','HTML/map project anchor');
  if(enabled.includes('district'))CG.dist.forEach(function(o,i){
    add('district','polygon',meters(o.xy),CG.districtName||'','HTML/CITY.district',
        i===0&&o.c?{labelAt:meters(o.c)}:null);
  });
  var verifiedPoints=pf.verifiedPOIs||[];
  var pointLayers={
    rail:PXc.rail, air:PXc.air, metro:PXc.metro, bus:PX.transit,
    shop:PX.amen.shop, gov:PX.amen.gov, sport:PX.amen.sport, park:PX.amen.park
  };
  Object.keys(pointLayers).forEach(function(layer){
    if(!enabled.includes(layer)||verifiedPoints.some(function(o){return (o.layer||'shop')===layer;}))return;
    pointLayers[layer].forEach(function(o){add(layer,'point',[o.x,o.y],o.src.name,'HTML/AMap POI/'+layer);});
  });
  if(enabled.includes('express'))CG.exp.forEach(function(o){add('express','line',meters(o.xy),o.name,'HTML/CG.exp');});
  if(enabled.includes('road'))GEO.roadL.forEach(function(o){
    add('road','line',meters(o.xy),'','HTML/GEO.roadL',{level:o.lv||0});
  });
  ['green','water'].forEach(function(k){(PX[k]||[]).forEach(function(o){add(k,'polygon',o.xy,'','HTML/PX.'+k);});});
  (GEO.waterways||[]).forEach(function(o){
    add('water','line',meters(o.xy),o.name||'','HTML/GEO.waterways',{class:o.cls,measuredWidthM:o.measured,defaultWidthM:o.w});
  });
  (L.waterNames||[]).forEach(function(o){
    add('water','point',point(o.lng,o.lat),o.name,'HTML/L.waterNames',{kindOfWater:o.kind});
  });
  [['express',PXc.expLabels,'HTML/PXc.expLabels'],['road',PX.roadNames,'HTML/PX.roadNames']].forEach(function(group){
    var layer=group[0];
    if(!enabled.includes(layer))return;
    (group[1]||[]).forEach(function(o){
      add(layer,'point',[o.x,o.y],o.src.name,group[2]);
    });
  });
  (pf.rings||[]).forEach(function(o){var a=[];for(var i=0;i<=120;i++){var p=map.lngLatToContainer(circlePoint(SITE.lng,SITE.lat,o.m,i*3));a.push(p.x,p.y);}add('ring','line',a,o.label,'HTML/circlePoint estimate',{labelAt:[baseP0.x+12,baseP0.y-o.m/metersPerPixel()+12]});});
  (pf.verifiedLabels||[]).forEach(function(o){add(o.layer,'point',point(o.lng,o.lat),o.name,'HTML/config verified label',{evidence:o.evidence});});
  verifiedPoints.forEach(function(o){add(o.layer||'shop','point',point(o.lng,o.lat),o.name,'HTML/config verified POI',{evidence:o.evidence,number:o.number});});
  if(enabled.includes('view'))(window.SITE_VIEWS||[]).forEach(function(o){
    var p=meters([o.mx,o.my]),d=[o.dx,-o.dy],end=[p[0]+d[0]*48,p[1]+d[1]*48];
    add('view','polygon',[p[0],p[1],end[0]-d[1]*14,end[1]+d[0]*14,end[0]+d[1]*14,end[1]-d[0]*14],String(o.i+1),'HTML/SITE_VIEWS',{labelAt:[p[0]+12,p[1]+18]});
  });
  // External planning polygons must carry independently checked planning provenance.
  (pf.planningFeatures||[]).forEach(function(o){if(!o.evidence||o.evidence.status!=='verified'||!o.evidence.registration)throw Error('Planning provenance missing');var a=[];o.coordinates.forEach(function(p){a.push(...point(p[0],p[1]));});add('planning','polygon',a,o.name,'HTML/registered planning input',{evidence:o.evidence});});
  (pf.resourceFeatures||[]).forEach(function(o){
    if(!enabled.includes('resource'))return;
    if(!o.evidence||o.evidence.status!=='verified'||!o.evidence.registration)throw Error('Resource provenance missing');
    var rings=o.kind==='polygon'?(o.rings||[o.coordinates]).map(function(r){return r.flatMap(function(p){return point(p[0],p[1]);});}):null;
    var a=rings?rings[0]:o.kind==='point'?point(o.coordinates[0],o.coordinates[1]):o.coordinates.flatMap(function(p){return point(p[0],p[1]);});
    add('resource',o.kind,a,o.name,'HTML/registered resource',{style_key:o.style_key,resource_id:o.resource_id,source_feature_id:o.id,evidence:o.evidence,rings:rings,labelAt:o.label_anchor?point(o.label_anchor[0],o.label_anchor[1]):null});
  });
  return out;
}
window.PPT_EXPORT={
 selectPage:function(n){pptPage=n;var pf=CFG2.ppt.pages[n];if(!pf)throw Error('Missing page profile');
  gray.on = pf.basemapStyle ? pf.basemapStyle === 'grayscale' : !!CFG2.basemap.gray;
  applyGray(); paintSw(document.getElementById('gray-sw'), gray.on);
  CFG2.layers=JSON.parse(JSON.stringify(pf.layers));Object.keys(vis).forEach(function(k){vis[k]=pf.enabled.includes(k);});fitRadius(pf.radius);setTimeout(function(){project();draw(false);},400);return n;},
 snapshot:function(){
  if(pptPage===null)throw Error('请先选择要采集的页面');
  if(!map||building||!L)throw Error('地图尚未完成构建');
  if(Math.hypot(SITE.lng-captureSite[0],SITE.lat-captureSite[1])>0.000001)
    throw Error('基地位置已改变，请按新坐标重新生成项目网页');
  project();
  return JSON.parse(JSON.stringify({schema:3,page:pptPage,crs:'GCJ02',capturedAt:new Date().toISOString(),site:SITE,viewport:{width:innerWidth,height:innerHeight,dpr:DPR},center:map.getCenter(),zoom:map.getZoom(),mpp:metersPerPixel(),p0:baseP0,cornerGCJ:[[0,0],[innerWidth,0],[0,innerHeight],[innerWidth,innerHeight]].map(function(p){var q=map.containerToLngLat(new AMap.Pixel(...p));return [q.lng,q.lat];}),geographic:{layers:L,city:CITY},screen:{geometry:GEO,local:PX,city:PXc,regional:CG},features:pptFeatures(),views:window.SITE_VIEWS||[],photosPresent:Object.keys(window.SITE_PHOTOS||{}),buildLog:window.__sbLog||[],error:window.__buildErr||null}));
 },
 basemapOnly:function(on){pptOnlyBase=on;document.body.classList.toggle("capture-basemap",on);if(on&&baseMode!=='satellite')setBase('satellite');['ov','panel','legend','views','ui-toggle','hint','loading','ppt-preview'].forEach(function(id){var e=document.getElementById(id);if(e)e.style.setProperty('display',on?'none':'','important');});},
 download:function(){var a=document.createElement('a');a.href=URL.createObjectURL(new Blob([JSON.stringify(this.snapshot(),null,2)],{type:'application/json'}));a.download='P'+pptPage+'-layers.json';a.click();}
};
