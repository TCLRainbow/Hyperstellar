from urllib import request
import urllib
import json
import os
from zipfile import ZipFile

try:
    os.environ['DOTNET_ROOT'] = os.environ['HOME'] + '/.dotnet'
except KeyError:
    pass

RUNS_URL = 'https://api.github.com/repos/TCLRainbow/Hyperstellar/actions/workflows/82482571/runs'
BRANCH = 'main'


def explode(cond, msg):
    if cond:
        print('ERROR: ' + msg)
        os._exit(1)


def build_artifact_url(run_id):
    return f'https://api.github.com/repos/TCLRainbow/Hyperstellar/actions/runs/{run_id}/artifacts'


with open('hyperstellar-token.txt') as f:
    token, current_title = f.readlines()
    token = token.rstrip('\n')

req = request.Request(RUNS_URL)
with request.urlopen(req) as resp:
    result = json.loads(resp.read())
run_count = result['total_count']
explode(run_count == 0, 'No workflow runs!')
print(f'There are {run_count} runs')

# Get latest run from branch
for run in result['workflow_runs']:
    if run['head_branch'] == BRANCH:
        break

gha_title = run['display_title']
run_branch = run['head_branch']
print(f'Checking run ({run_branch}): {gha_title}')
explode(run['status'] != 'completed', 'Run is not completed!')

if gha_title == current_title:
    print('Already downloaded.')
else:
    req = request.Request(run['jobs_url'])
    with request.urlopen(req) as resp:
        result = json.loads(resp.read())
    jobs = result['jobs']
    build_ok = False
    for job in jobs:
        if job['name'] == 'build':
            build_ok = True
            explode(job['conclusion'] != 'success', 'Build failed!')
            break
    explode(not build_ok, 'No build job found!')
    explode(run['pull_requests'], 'Triggered by PR')

    run_id = run['id']
    print('Checking artifact')
    req = request.Request(build_artifact_url(run_id))
    with request.urlopen(req) as resp:
        result = json.loads(resp.read())
    run_count = result['total_count']
    explode(run_count == 0, 'No artifacts found!')
    artifact = result['artifacts'][0]
    explode(artifact['name'] != 'Bot', 'Artifact name not Bot!')

    download_url = artifact['archive_download_url']
    req = request.Request(download_url, headers={'Authorization': f'Bearer {token}'})
    os.chdir('./Hyperstellar')
    print(f'Downloading {download_url}')
    try:
        with request.urlopen(req) as resp:
            with open('bot.zip', 'wb') as f:
                f.write(resp.read())
    except urllib.error.HTTPError as e:
        redirected_url = e.geturl()
        print(f'Redirecting to {redirected_url}')
        req = request.Request(redirected_url)
        with request.urlopen(req) as resp:
            with open('bot.zip', 'wb') as f:
                f.write(resp.read())

    print('Unzipping')
    with ZipFile('bot.zip') as f:
        f.extractall()
    print('Removing zip')
    os.remove('bot.zip')

    os.chdir('..')
    with open('hyperstellar-token.txt', 'w') as f:
        f.write(f'{token}\n{gha_title}')
    print('Updated token.txt')

os.chdir('./Hyperstellar')
print('Running')
os.execv('Bot', ['./Bot'])