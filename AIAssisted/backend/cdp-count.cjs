const {chromium}=require('C:/Users/ajaan/AppData/Roaming/npm/node_modules/playwright');
(async()=>{const b=await chromium.connectOverCDP('http://127.0.0.1:9222');console.log({contexts:b.contexts().length,pages:b.contexts().map(c=>c.pages().map(p=>{try{return new URL(p.url()).origin}catch{return 'no HTTP origin'}}))});await b.close()})().catch(e=>{console.error('CDP failed');process.exit(1)});
