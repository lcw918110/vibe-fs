import test from 'node:test'
import {fixture,assert,data,caseName,render,command,stopped,existsSync,readFileSync} from './support/command-program.fixture.mjs'

test('WHAT[repository-programming-051] send adds a newline without resuming the named command continuation', {timeout:12000}, async t=>{
  const h=fixture(t,'051-input','DevOps')
  const cmd=command(h,'reader',String.raw`fs.writeFileSync(file('reader.pid'),String(process.pid));process.stdin.setEncoding('utf8');let input='';process.stdin.on('data',part=>{input+=part;if(input.includes('\n')){fs.writeFileSync(file('input'),input);process.stdout.write(input,()=>process.exit(0));}});`)
  const first=await h.run('reader','const r=await this.call('+JSON.stringify(cmd)+');this.write("after",r.stdout);return r.stdout;',{intent:'w'.repeat(20)})
  assert.equal(caseName(first),'Parked',render(first))
  const sent=data(await h.run('sender','return await this.send("reader","hello");'))
  assert.equal(sent.sent,true)
  await stopped(h,'reader')
  assert.equal(readFileSync(h.file('input'),'utf8'),'hello\n')
  assert.equal(existsSync(h.file('after')),false)
  const done=data(await h.unpark('reader'))
  assert.equal(done.state,'completed');assert.equal(done.value,'hello\n')
  assert.equal(readFileSync(h.file('after'),'utf8'),'hello\n')
})

test('WHAT[repository-programming-051] signal preserves the true null exit code and leaves the target parked', {timeout:12000}, async t=>{
  const h=fixture(t,'051-signal','DevOps')
  const cmd=command(h,'server',"fs.writeFileSync(file('server.pid'),String(process.pid));setInterval(()=>{},1000);")
  assert.equal(caseName(await h.run('server','const r=await this.call('+JSON.stringify(cmd)+');this.write("after",JSON.stringify({exitCode:r.exitCode,signal:r.signal}));return "done";',{intent:'w'.repeat(20)})),'Parked')
  assert.equal(data(await h.run('signal','return await this.signal("server","SIGTERM");')).sent,true)
  await stopped(h,'server')
  assert.equal(existsSync(h.file('after')),false)
  assert.equal(data(await h.unpark('server')).state,'completed')
  assert.deepEqual(JSON.parse(readFileSync(h.file('after'),'utf8')),{exitCode:null,signal:'SIGTERM'})
})

test('WHAT[repository-programming-051] no-command controls preserve the existing explicit pause',async t=>{
  const h=fixture(t,'051-no-command','DevOps')
  assert.equal(caseName(await h.run('manual','return await this.park("ready");')),'Parked')
  for(const member of ['send','signal']) {
    const response=data(await h.run(member,'return await this.'+member+'("manual","SIGTERM");'))
    assert.equal(response.code,'CALL_CHILD_UNKNOWN')
    assert.equal(response.name,'manual')
  }
  const done=data(await h.unpark('manual','accepted'))
  assert.equal(done.state,'completed');assert.equal(done.value,'accepted')
})
