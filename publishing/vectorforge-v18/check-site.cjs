const fs = require('node:fs');
const { chromium } = require('C:/Users/zhangweixsw/.cache/codex-runtimes/codex-primary-runtime/dependencies/node/node_modules/playwright');
(async()=>{
  const browser=await chromium.launch({channel:'msedge',headless:true});
  const output=[];
  for(const width of [1280,390]){
    const context=await browser.newContext({viewport:{width,height:900},deviceScaleFactor:1});
    for(const [name,url] of Object.entries({product:'https://mootop.top/software/vectorforge/',guide:'https://mootop.top/docs/vectorforge/',story:'https://mootop.top/vectorforge-v18-0-1-development-story/',catalog:'https://mootop.top/software/'})){
      const page=await context.newPage(); const response=await page.goto(url,{waitUntil:'domcontentloaded',timeout:60000});
      await page.locator('img').evaluateAll(imgs=>imgs.forEach(i=>{i.loading='eager'}));
      await page.waitForTimeout(3000);
      await page.evaluate(async()=>{for(let y=0;y<document.documentElement.scrollHeight;y+=600){scrollTo(0,y);await new Promise(r=>setTimeout(r,100))}scrollTo(0,0)});
      await page.waitForTimeout(1500);
      await page.evaluate(()=>Promise.all([...document.querySelectorAll('.mootop-vectorforge img,[data-product="vectorforge"] img')].map(i=>Promise.race([i.decode().catch(()=>{}),new Promise(r=>setTimeout(r,20000))]))));
      const state=await page.evaluate(()=>({title:document.title,width:innerWidth,scroll:document.documentElement.scrollWidth,
        main:document.querySelector('main')?.getBoundingClientRect().width,
        story:document.querySelector('.mootop-editorial-story')?.getBoundingClientRect().width,
        missingImages:[...document.querySelectorAll('img')].filter(i=>!i.complete||!i.naturalWidth).map(i=>i.src),
        vectorforgeCards:document.querySelectorAll('[data-mootop-product="vectorforge"],[data-product="vectorforge"]').length,
        cartButtons:[...document.querySelectorAll('a,button')].filter(a=>a.textContent.includes('加入购物车')).length,
        images:[...document.querySelectorAll('.mootop-vectorforge img,[data-mootop-product="vectorforge"] img,[data-product="vectorforge"] img')].map(i=>({src:i.src,fit:getComputedStyle(i).objectFit,width:i.getBoundingClientRect().width,height:i.getBoundingClientRect().height}))}));
      output.push({name,status:response.status(),...state});
      await page.screenshot({path:`artifacts/vectorforge-site-${name}-${width}.png`,fullPage:true});
      await page.close();
    }
    await context.close();
  }
  await browser.close();fs.writeFileSync('artifacts/vectorforge-site-qa.json',JSON.stringify(output,null,2));
  console.log(JSON.stringify(output,null,2));
  if(output.some(r=>r.status!==200||r.scroll>r.width||r.missingImages.some(src=>r.name!=='catalog'||src.includes('/vectorforge/'))||r.cartButtons||(r.name==='catalog'&&r.vectorforgeCards!==1)))process.exitCode=1;
})().catch(error=>{console.error(error);process.exitCode=1});
